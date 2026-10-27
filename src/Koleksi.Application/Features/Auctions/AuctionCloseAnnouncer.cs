using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Application.Features.Auctions;

/// <summary>
/// What happens around the world once an auction has closed and the close is saved: the lot leaves
/// browse, the page's watchers are told, and the winner and the seller are notified.
/// </summary>
/// <remarks>
/// <para>
/// One implementation for every way an auction closes. The close sweep (<c>CloseDueAuctionsHandler</c>)
/// and a buyout (<c>BuyoutHandler</c>) both end an auction, and the buyout used to stop at the save:
/// the lot stayed in browse and "ending soon", and neither the winner nor the seller was told
/// (QA D-03). They now share this.
/// </para>
/// <para>
/// <b>Best effort, never throws.</b> The close is already true once it is saved; telling browsers about
/// it is a push like every other, and a missed one is repaired by the page's next load. Removing an
/// absent browse row is a no-op, so calling this twice is harmless.
/// </para>
/// </remarks>
public sealed partial class AuctionCloseAnnouncer(
    IListingRepository listings,
    IListingSearchIndex searchIndex,
    IRealtimeBroadcaster realtime,
    ILogger<AuctionCloseAnnouncer> logger)
{
    /// <summary>Private signal to the winner: pay now.</summary>
    public const string WonEvent = "auction-won";

    /// <summary>Private signal to the seller: the lot sold.</summary>
    public const string SoldEvent = "auction-sold";

    /// <param name="auction">The auction after it has been closed (Ended or EndedWithoutSale) and saved.</param>
    public async Task AnnounceAsync(Auction auction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auction);

        // A closed auction is no longer something to bid on, so it leaves browse and "ending soon"
        // (the sold lot is still reachable by its link, and by the winner's checkout, which read
        // the listing and auction rows, not this projection). Removing an absent row is a no-op.
        try
        {
            await searchIndex.RemoveAsync(auction.ListingId, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogBrowseRemovalFailed(logger, auction.ListingId, failure);
        }

        await PublishAsync(
            RealtimeTopics.Auction(auction.ListingId),
            RealtimeAuctionEventPublisher.ClosedEvent,
            new AuctionClosedEvent(auction.Status.ToString(), auction.CurrentPrice.Rupiah, auction.BidCount),
            auction.ListingId,
            ct).ConfigureAwait(false);

        if (auction.Status != AuctionStatus.Ended || auction.LeadingBidderId is not { } winnerId)
        {
            return;
        }

        string title;
        try
        {
            var listing = await listings.FindAsync(auction.ListingId, ct).ConfigureAwait(false);
            if (listing is null)
            {
                return;
            }

            title = listing.Title;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogNoticeFailed(logger, auction.ListingId, failure);
            return;
        }

        // The auction page, as the frontend routes it. The slug is cosmetic: the id resolves.
        var notice = new AuctionResultEvent(
            auction.ListingId,
            title,
            auction.CurrentPrice.Rupiah,
            $"/lelang/{Slug.From(title)}-{auction.ListingId}");

        // Each goes to one person's private channel and carries only what that person is already
        // entitled to: the lot, the price, the link. The seller's notice does not name the winner.
        await PublishAsync(RealtimeTopics.User(winnerId), WonEvent, notice, auction.ListingId, ct).ConfigureAwait(false);
        await PublishAsync(RealtimeTopics.User(auction.SellerId), SoldEvent, notice, auction.ListingId, ct).ConfigureAwait(false);
    }

    private async Task PublishAsync(string topic, string type, object data, string listingId, CancellationToken ct)
    {
        try
        {
            await realtime.PublishAsync(topic, type, data, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            LogPushFailed(logger, listingId, type, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Auction {ListingId} closed, but removing it from browse failed.")]
    private static partial void LogBrowseRemovalFailed(ILogger logger, string listingId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Auction {ListingId} closed, but reading its listing for the winner and seller notices failed.")]
    private static partial void LogNoticeFailed(ILogger logger, string listingId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Auction {ListingId} closed, but pushing the {EventType} event failed.")]
    private static partial void LogPushFailed(ILogger logger, string listingId, string eventType, Exception exception);
}
