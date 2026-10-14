using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Auctions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Sweeps;

/// <summary>
/// Closes auctions whose end time has passed, which nothing else in the product does.
/// </summary>
/// <remarks>
/// <para>
/// Bidding is refused by the clock the moment <c>EndsAt</c> passes, but an auction only becomes
/// <c>Ended</c> (a winner who can check out) or <c>EndedWithoutSale</c> when something closes it.
/// This is that something, driven by the schedule rather than by page views so a lot nobody is
/// looking at still settles on time.
/// </para>
/// <para>
/// <b>Safe to repeat and to race.</b> The due list comes from an eventually consistent index, so
/// each auction is re-read consistently before it is closed, and the save is version-conditioned
/// (<see cref="IAuctionRepository.SaveBidAsync"/> with no new bids is the status-only save). A bid
/// that lands first, or another sweep that closes it first, makes the save conflict; the item is
/// skipped and, if it is still due, found again next run. A second run over closed auctions finds
/// nothing, because a closed auction leaves the sparse index.
/// </para>
/// <para>
/// <b>Pushes never fail the sweep.</b> Once the close is saved it is true; telling browsers about
/// it is best effort, like every other publish. A missed push is repaired by the page's next load.
/// </para>
/// </remarks>
public sealed partial class CloseDueAuctionsHandler(
    IAuctionRepository auctions,
    AuctionCloseAnnouncer announcer,
    IClock clock,
    ILogger<CloseDueAuctionsHandler> logger)
{
    public async Task<CloseDueAuctionsResult> RunAsync(SweepBudget budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(budget);

        var due = await auctions.ListDueForCloseAsync(clock.UtcNow, budget.MaxItemsPerDuty, ct).ConfigureAwait(false);

        int sold = 0, unsold = 0, skipped = 0, errors = 0;

        foreach (var candidate in due)
        {
            ct.ThrowIfCancellationRequested();

            if (budget.IsTimeUp)
            {
                LogTimeUp(logger, due.Count);
                break;
            }

            try
            {
                switch (await CloseOneAsync(candidate.ListingId, ct).ConfigureAwait(false))
                {
                    case AuctionStatus.Ended:
                        sold++;
                        break;
                    case AuctionStatus.EndedWithoutSale:
                        unsold++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (ConcurrencyConflictException)
            {
                // A bid or another sweep got there first. Whatever is still due is found again.
                skipped++;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors++;
                LogItemFailed(logger, candidate.ListingId, failure);
            }
        }

        LogSummary(logger, due.Count, sold, unsold, skipped, errors);
        return new CloseDueAuctionsResult(sold, unsold, skipped, errors);
    }

    /// <returns>The status it was closed to, or <c>null</c> when there was nothing to close after all.</returns>
    private async Task<AuctionStatus?> CloseOneAsync(string listingId, CancellationToken ct)
    {
        // The index entry can lag: a bid may have extended the auction, or someone may have closed
        // it, since the query. A consistent read decides, and the version check backs it.
        var auction = await auctions.FindAsync(listingId, ct).ConfigureAwait(false);
        if (auction is null || auction.Status != AuctionStatus.Live || clock.UtcNow < auction.EndsAt)
        {
            return null;
        }

        var expectedVersion = auction.Version;
        auction.Close(clock);
        await auctions.SaveBidAsync(auction, [], expectedVersion, ct).ConfigureAwait(false);

        await announcer.AnnounceAsync(auction, ct).ConfigureAwait(false);
        return auction.Status;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep close-auctions: {Due} due, {Sold} sold, {Unsold} without sale, {Skipped} skipped, {Errors} failed.")]
    private static partial void LogSummary(ILogger logger, int due, int sold, int unsold, int skipped, int errors);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sweep close-auctions: could not close auction {ListingId}; it stays due for the next run.")]
    private static partial void LogItemFailed(ILogger logger, string listingId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sweep close-auctions: time allowance used up with {Due} due; the rest wait for the next run.")]
    private static partial void LogTimeUp(ILogger logger, int due);
}

/// <param name="Sold">Closed with a winner.</param>
/// <param name="Unsold">Closed with no bids, or below reserve.</param>
/// <param name="Skipped">Found due but already closed, extended, or lost to a concurrent writer.</param>
/// <param name="Errors">Failed unexpectedly; still due next run.</param>
public sealed record CloseDueAuctionsResult(int Sold, int Unsold, int Skipped, int Errors);

/// <summary>
/// The public "this auction is over" event on the auction's topic. Only what the page already
/// shows: the outcome and the final visible price. Never the winner.
/// </summary>
public sealed record AuctionClosedEvent(string Status, long CurrentPriceRupiah, int BidCount);

/// <summary>
/// A private notice to the winner or the seller of a closed auction.
/// </summary>
/// <param name="ListingId">The lot, so a newer notice for the same lot replaces an older one.</param>
/// <param name="Title">The lot's title.</param>
/// <param name="AmountRupiah">The winning price.</param>
/// <param name="Path">The auction page, for the link.</param>
public sealed record AuctionResultEvent(string ListingId, string Title, long AmountRupiah, string Path);
