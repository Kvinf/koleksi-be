namespace Koleksi.Application.Abstractions;

/// <summary>
/// Publishes a live auction update to everyone currently watching that listing's page.
/// </summary>
/// <remarks>
/// <para>
/// Implemented as an in-process broadcast — see the concrete type in <c>Koleksi.Api</c> —
/// which is honest about the current deployment rather than aspirational: one API instance,
/// one set of open connections. A multi-instance deployment would need this behind a real
/// fan-out (SNS+SQS, or a managed WebSocket/AppSync service), but nothing above this
/// interface would change to get there.
/// </para>
/// <para>
/// Only ever carries what is already public on the item page — the visible price, the bid
/// count, the masked bidder alias. Never the leader's identity or standing bid: the same
/// boundary <see cref="Koleksi.Domain.Auctions.Auction.LeadingBidderId"/>'s own remarks
/// describe, just enforced at a second call site.
/// </para>
/// </remarks>
public interface IAuctionEventPublisher
{
    /// <summary>Best-effort. A failure here must never fail the bid that triggered it.</summary>
    Task PublishAsync(string listingId, AuctionPriceUpdate update, CancellationToken ct);
}

/// <param name="CurrentPriceRupiah">The new visible price.</param>
/// <param name="NextMinimumBidRupiah">What the next bidder must beat.</param>
/// <param name="BidCount">Total bids so far.</param>
/// <param name="EndsAt">Close time, which anti-sniping may have just moved.</param>
/// <param name="ReserveMet">Whether the reserve, if any, has now been met.</param>
/// <param name="MaskedBidder">The masked handle of whoever just placed this bid.</param>
/// <param name="PlacedAt">When this bid was accepted.</param>
public sealed record AuctionPriceUpdate(
    long CurrentPriceRupiah,
    long NextMinimumBidRupiah,
    int BidCount,
    DateTimeOffset EndsAt,
    bool ReserveMet,
    string MaskedBidder,
    DateTimeOffset PlacedAt);
