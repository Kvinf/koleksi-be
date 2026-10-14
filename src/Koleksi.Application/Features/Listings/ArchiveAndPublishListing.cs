using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Application.Features.Listings;

/// <param name="Id">The listing.</param>
/// <param name="Status">Its status after the call.</param>
public sealed record ListingStatusResult(string Id, string Status);

/// <summary>
/// Delists a listing — the "delist" side of list/delist — refusing while money is in flight on it.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is the caller's job (the endpoint checks it before its idempotency wrapper, so a replay
/// cannot short-circuit it). This is what is left: may this listing be pulled right now?
/// </para>
/// <para>
/// <b>Money in flight blocks it (D-01).</b> Delisting while a buyer is paying let a seller walk away
/// from a deal, or republish and sell the same one-of-one item twice. So it is refused with
/// <c>listing.has_pending_sale</c> while the item is held (a buyer's order awaits payment: every order
/// takes a hold for its payment window, so a hold stands in for "an unpaid order exists") or while an
/// accepted offer is waiting for its checkout, and with <c>auction.awaiting_payment</c> while an
/// auction's winner still has time to pay. An auction with bids is refused as before
/// (<c>auction.has_bids</c>). Once a winner's time has lapsed the item is the seller's again.
/// </para>
/// <para>
/// A payment that still lands after a delisting (a hold that lapsed first) is not lost: payment is
/// authoritative, and <see cref="Listing.MarkSold"/> sells an archived listing.
/// </para>
/// </remarks>
public sealed class ArchiveListingHandler(
    IListingRepository listings,
    IAuctionRepository auctions,
    IOfferRepository offers,
    IListingHoldStore holds,
    IListingSearchIndex searchIndex,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<ListingStatusResult> HandleAsync(Listing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var userId = currentUser.RequireUserId();
        listing.EnsureOwnedBy(userId);

        // Only a listing that is for sale can have a sale in flight. Anything else (a draft, an already
        // archived one, a sold one that Listing.Archive refuses) goes straight to the domain rule.
        if (listing.Status == ListingStatus.Active)
        {
            await EnsureNoSaleInFlightAsync(listing, ct).ConfigureAwait(false);

            // An auction keeps taking bids whatever its listing says, so delisting has to deal with it.
            // With bids it is refused: bidders have committed real money, and a seller who could pull
            // the lot after seeing the price would be running free price discovery (Auction.Cancel's
            // own rule, `auction.has_bids`). Without bids the auction is cancelled so nobody can bid on
            // a listing that is no longer there. A closed auction needs no change here.
            if (listing.Format == ListingFormat.Auction
                && await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false) is { } auction)
            {
                if (auction.Status == AuctionStatus.Ended && !auction.IsWinnerPaymentLapsed(clock))
                {
                    throw DomainException.For(
                        "auction.awaiting_payment",
                        "This auction ended with a winner who still has time to pay.");
                }

                if (auction.Status is AuctionStatus.Live or AuctionStatus.Scheduled)
                {
                    var auctionVersion = auction.Version;
                    auction.Cancel(userId);

                    // No new bid rows: this call is the version-checked save of the auction itself, and
                    // fails if a bid slipped in since it was read.
                    await auctions.SaveBidAsync(auction, [], auctionVersion, ct).ConfigureAwait(false);
                }
            }
        }

        var expectedVersion = listing.Version;
        listing.Archive(clock);
        await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

        // Withdrawing a listing must also withdraw it from browse — an archived item still sitting in
        // the search index is a dead link away from a page that now refuses to render it.
        await searchIndex.RemoveAsync(listing.Id, ct).ConfigureAwait(false);

        return new ListingStatusResult(listing.Id, listing.Status.ToString());
    }

    private async Task EnsureNoSaleInFlightAsync(Listing listing, CancellationToken ct)
    {
        if (await holds.IsHeldAsync(listing.Id, ct).ConfigureAwait(false))
        {
            throw PendingSale();
        }

        // An accepted offer is a price the seller has committed to and the buyer has a day or so to
        // pay. Pulling the item would make the deal dead on arrival (checkout refuses an unavailable
        // listing). Judged by the deadline, not the stored status: a lapsed deal is no deal.
        foreach (var offer in await offers.ListOpenForListingAsync(listing.Id, ct).ConfigureAwait(false))
        {
            if (offer.EffectiveStatus(clock) == OfferStatus.Accepted)
            {
                throw PendingSale();
            }
        }
    }

    private static DomainException PendingSale() => DomainException.For(
        "listing.has_pending_sale",
        "A buyer is paying for this item, or has an accepted offer waiting. Try again once that is settled.");
}

/// <summary>
/// (Re-)publishes a draft or archived listing — the "list" side of list/delist — and puts it back in browse.
/// </summary>
/// <remarks>
/// <see cref="Listing.Publish"/> already accepts either starting state, refuses a sold listing, and
/// throws <c>listing.photo_required</c> if there is nothing to show a buyer. This adds what an auction
/// needs and the order of writes: everything that can refuse the publish is decided in memory first
/// (<see cref="AuctionLifecycle.PlanAsync"/>), then the auction is written, then the listing, then browse
/// (D-09, D-05).
/// </remarks>
public sealed class PublishListingHandler(
    IListingRepository listings,
    IAuctionRepository auctions,
    IListingSearchIndex searchIndex,
    ICurrentUser currentUser,
    IClock clock,
    PublicIdentityResolver identities)
{
    public async Task<ListingStatusResult> HandleAsync(Listing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);
        listing.EnsureOwnedBy(currentUser.RequireUserId());

        var sellerUsername = (await identities.ResolveAsync(listing.SellerId, ct).ConfigureAwait(false)).Handle;

        var expectedVersion = listing.Version;
        var neverLive = listing.IsUnpublished;
        listing.Publish(clock);

        var plan = await AuctionLifecycle.PlanAsync(auctions, listing, neverLive, clock, ct).ConfigureAwait(false);

        // The auction first, the listing second: see AuctionLifecycle.CommitAsync.
        await AuctionLifecycle.CommitAsync(auctions, plan, ct).ConfigureAwait(false);
        await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

        // The mirror image of the archive's RemoveAsync: relisting must put the listing back in
        // browse, not just flip its own status column.
        await searchIndex.IndexAsync(
            new ListingIndexEntry(listing, sellerUsername, plan.Auction?.EndsAt, plan.Auction?.BidCount ?? 0), ct)
            .ConfigureAwait(false);

        return new ListingStatusResult(listing.Id, listing.Status.ToString());
    }
}
