using Koleksi.Application.Abstractions;

namespace Koleksi.Application.Features.Auctions;

/// <summary>
/// Publishes an auction's price update on the listing's public topic, so every browser
/// watching that lot receives it.
/// </summary>
/// <remarks>
/// What goes out is <see cref="AuctionPriceUpdate"/>, which by construction holds only what
/// the item page already shows; see that type for the boundary this call site enforces.
/// </remarks>
public sealed class RealtimeAuctionEventPublisher(IRealtimeBroadcaster realtime) : IAuctionEventPublisher
{
    public const string PriceEvent = "price";

    /// <summary>Published by the close sweep when the auction ends; the page re-reads its data on it.</summary>
    public const string ClosedEvent = "closed";

    public Task PublishAsync(string listingId, AuctionPriceUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        return realtime.PublishAsync(RealtimeTopics.Auction(listingId), PriceEvent, update, ct);
    }
}
