using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Auctions;

/// <summary>
/// Ends an auction immediately as a sale at its buyout price, on behalf of the
/// authenticated caller.
/// </summary>
/// <remarks>
/// Same shape as <see cref="PlaceBidHandler"/>, including the retry-on-conflict loop: a
/// buyout still writes the auction's version, so it can still race a bid landing in the
/// same instant, and the correct answer to that race is to re-read and re-run the domain
/// rule, not to merge two writers' intent.
/// </remarks>
public sealed partial class BuyoutHandler(
    IAuctionRepository auctions,
    IListingSearchIndex searchIndex,
    IAuctionEventPublisher events,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<BuyoutHandler> logger)
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Buyout on {ListingId} lost a concurrency race on attempt {Attempt}; retrying.")]
    private static partial void LogRetry(ILogger logger, string listingId, int attempt);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Buyout on {ListingId} saved, but updating its browse-index price failed.")]
    private static partial void LogPriceIndexFailed(ILogger logger, string listingId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Buyout on {ListingId} saved, but publishing the live update failed.")]
    private static partial void LogEventPublishFailed(ILogger logger, string listingId, Exception exception);

    private const int MaxAttempts = 4;

    public async Task<BuyoutResult> HandleAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var buyerId = currentUser.RequireUserId();
        var buyerUsername = currentUser.Username
            ?? throw DomainException.For("auction.no_username", "Your profile is missing a username.");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var auction = await auctions.FindAsync(listingId, ct).ConfigureAwait(false);
            if (auction is null)
            {
                return BuyoutResult.NotFound;
            }

            var expectedVersion = auction.Version;

            // Throws DomainException for every rule violation — no buyout price, not
            // live, the seller trying to buy their own lot — propagated unchanged.
            var outcome = auction.Buyout(buyerId, buyerUsername, clock);

            try
            {
                await auctions.SaveBidAsync(auction, outcome.Bids, expectedVersion, ct).ConfigureAwait(false);

                try
                {
                    await searchIndex
                        .UpdatePriceAsync(listingId, outcome.CurrentPrice.Rupiah, auction.BidCount, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    LogPriceIndexFailed(logger, listingId, failure);
                }

                try
                {
                    await events.PublishAsync(
                        listingId,
                        new AuctionPriceUpdate(
                            outcome.CurrentPrice.Rupiah,
                            auction.NextMinimumBid.Rupiah,
                            auction.BidCount,
                            outcome.EndsAt,
                            auction.ReserveMet,
                            BidderAlias.Mask(buyerUsername),
                            outcome.Bids[0].PlacedAt),
                        ct).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    LogEventPublishFailed(logger, listingId, failure);
                }

                return new BuyoutResult(Found: true, FinalPriceRupiah: outcome.CurrentPrice.Rupiah);
            }
            catch (ConcurrencyConflictException)
            {
                if (attempt >= MaxAttempts)
                {
                    break;
                }

                LogRetry(logger, listingId, attempt);
            }
        }

        throw DomainException.For(
            "auction.too_busy", "This auction is receiving a lot of activity right now. Please try again.");
    }
}

/// <param name="Found"><c>false</c> when the auction does not exist.</param>
/// <param name="FinalPriceRupiah">The buyout price the auction sold for.</param>
public sealed record BuyoutResult(bool Found, long FinalPriceRupiah)
{
    public static BuyoutResult NotFound { get; } = new(false, 0);
}
