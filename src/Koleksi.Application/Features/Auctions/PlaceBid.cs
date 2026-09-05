using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Auctions;

/// <summary>
/// Places a bid on behalf of the authenticated caller.
/// </summary>
/// <remarks>
/// <para>
/// The bidder's identity comes from <see cref="ICurrentUser"/>, never from the request.
/// A request body that named its own bidder would let anyone bid as anyone.
/// </para>
/// <para>
/// Bidding is the one genuinely contended write in the product — the last minute of a
/// popular lot is exactly when two bids arrive in the same instant. The write is
/// conditioned on the auction's version, and a lost race is retried against freshly
/// read state rather than merged, because merging two bids means inventing a price
/// neither bidder offered.
/// </para>
/// </remarks>
public sealed partial class PlaceBidHandler(
    IAuctionRepository auctions,
    IListingSearchIndex searchIndex,
    IAuctionEventPublisher events,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<PlaceBidHandler> logger)
{
    // Source-generated log methods. Bidding is the hottest write path in the product,
    // and these avoid boxing the arguments and allocating a params array on every call
    // — including calls that are discarded because the level is disabled.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Bid on {ListingId} lost a concurrency race on attempt {Attempt}; retrying.")]
    private static partial void LogBidRetry(ILogger logger, string listingId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Bid on {ListingId} exhausted {MaxAttempts} attempts under contention.")]
    private static partial void LogBidContentionExhausted(ILogger logger, string listingId, int maxAttempts);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Bid on {ListingId} saved, but updating its browse-index price failed.")]
    private static partial void LogPriceIndexFailed(ILogger logger, string listingId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Bid on {ListingId} saved, but publishing the live update failed.")]
    private static partial void LogEventPublishFailed(ILogger logger, string listingId, Exception exception);

    /// <summary>
    /// How many times to re-read and retry when another bid lands first.
    /// </summary>
    /// <remarks>
    /// Each attempt re-runs the domain rules against the new price, so a retry can
    /// legitimately fail with "bid too low" — that is the correct answer for a bidder
    /// whose maximum was passed while their request was in flight, not an error to hide.
    /// </remarks>
    private const int MaxAttempts = 4;

    public async Task<PlaceBidResult> HandleAsync(PlaceBidCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var bidderId = currentUser.RequireUserId();
        var bidderUsername = currentUser.Username
            ?? throw DomainException.For("auction.no_username", "Your profile is missing a username.");

        var amount = Money.TryFromRupiah(command.AmountRupiah)
            ?? throw DomainException.For("auction.invalid_amount", "Bid amount must be a positive number.");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var auction = await auctions.FindAsync(command.ListingId, ct).ConfigureAwait(false);
            if (auction is null)
            {
                return PlaceBidResult.NotFound;
            }

            var expectedVersion = auction.Version;

            // Throws DomainException for any rule violation. Those are the caller's
            // problem and propagate unchanged — only concurrency is retried here.
            var outcome = auction.PlaceBid(bidderId, bidderUsername, amount, clock);

            try
            {
                await auctions
                    .SaveBidAsync(auction, outcome.Bids, expectedVersion, ct)
                    .ConfigureAwait(false);

                // Best-effort, and deliberately outside the retry loop above: the bid is
                // already saved by this point, and a browse card lagging one bid behind is
                // a far smaller problem than a saved bid answering with a 500.
                try
                {
                    await searchIndex
                        .UpdatePriceAsync(command.ListingId, outcome.CurrentPrice.Rupiah, auction.BidCount, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    LogPriceIndexFailed(logger, command.ListingId, failure);
                }

                try
                {
                    await events.PublishAsync(
                        command.ListingId,
                        new AuctionPriceUpdate(
                            outcome.CurrentPrice.Rupiah,
                            auction.NextMinimumBid.Rupiah,
                            auction.BidCount,
                            outcome.EndsAt,
                            auction.ReserveMet,
                            BidderAlias.Mask(bidderUsername),
                            outcome.Bids[0].PlacedAt),
                        ct).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    LogEventPublishFailed(logger, command.ListingId, failure);
                }

                return new PlaceBidResult(
                    Found: true,
                    IsLeading: outcome.IsLeading,
                    CurrentPriceRupiah: outcome.CurrentPrice.Rupiah,
                    NextMinimumBidRupiah: auction.NextMinimumBid.Rupiah,
                    BidCount: auction.BidCount,
                    EndsAt: outcome.EndsAt,
                    WasExtended: outcome.WasExtended,
                    OutbidUserId: outcome.OutbidUserId);
            }
            catch (ConcurrencyConflictException)
            {
                if (attempt >= MaxAttempts)
                {
                    // Leave the loop rather than letting the exception escape. An escaping
                    // ConcurrencyConflictException becomes a 503, which reads as "the
                    // marketplace is down" for what is one popular lot under contention —
                    // and it would skip the warning below, the only operational signal
                    // that a lot has become too hot to bid on.
                    break;
                }

                // Expected under contention. Logged at debug so a hot auction does not
                // fill the log with noise that looks like a fault.
                LogBidRetry(logger, command.ListingId, attempt);
            }
        }

        LogBidContentionExhausted(logger, command.ListingId, MaxAttempts);

        throw DomainException.For(
            "auction.too_busy",
            "This auction is receiving a lot of bids right now. Please try again.");
    }
}

/// <param name="ListingId">The auction being bid on.</param>
/// <param name="AmountRupiah">The bid amount — becomes the new visible price outright.</param>
public sealed record PlaceBidCommand(string ListingId, long AmountRupiah);

/// <param name="Found"><c>false</c> when the auction does not exist.</param>
/// <param name="IsLeading">Whether the caller now holds the lead.</param>
/// <param name="CurrentPriceRupiah">The new visible price.</param>
/// <param name="NextMinimumBidRupiah">What the next bidder must beat.</param>
/// <param name="BidCount">Total bids placed.</param>
/// <param name="EndsAt">Close time, which anti-sniping may have moved.</param>
/// <param name="WasExtended">Whether this bid extended the auction.</param>
/// <param name="OutbidUserId">
/// Who just lost the lead, for notification. Server-side only — never serialise this
/// to the bidder, who has no business learning another bidder's identity.
/// </param>
public sealed record PlaceBidResult(
    bool Found,
    bool IsLeading,
    long CurrentPriceRupiah,
    long NextMinimumBidRupiah,
    int BidCount,
    DateTimeOffset EndsAt,
    bool WasExtended,
    string? OutbidUserId)
{
    public static PlaceBidResult NotFound { get; } =
        new(false, false, 0, 0, 0, default, false, null);
}
