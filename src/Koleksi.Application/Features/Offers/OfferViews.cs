using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Offers;

/// <summary>
/// Reads offers for the people on either side of them, with what the dashboard needs to show
/// a row: which item, who the other party is, and what each of them can do next.
/// </summary>
/// <remarks>
/// <para>
/// An offer's deadline is applied here, on read, and persisted (see <see cref="OfferExpiry"/>)
/// because no job does it. A list therefore never shows a lapsed offer as live, and a lapsed
/// pending offer stops blocking the buyer's next one.
/// </para>
/// <para>
/// The listing is read once per distinct id in a page, not once per row; a page is at most
/// <see cref="PageSize"/> offers, so this is a small, bounded number of point reads.
/// </para>
/// </remarks>
public sealed class OfferViews(
    IOfferRepository offers,
    IListingRepository listings,
    ICategoryRepository categories,
    IOrderRepository orders,
    PublicIdentityResolver identities,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int PageSize = 20;

    /// <summary>Offers the caller has made, newest first.</summary>
    public async Task<OfferViewPage> ListSentAsync(string? cursor, CancellationToken ct)
    {
        var viewerId = currentUser.RequireUserId();
        var page = await offers.ListForBuyerAsync(viewerId, cursor, PageSize, ct).ConfigureAwait(false);
        return await BuildPageAsync(page, viewerId, ct).ConfigureAwait(false);
    }

    /// <summary>Offers the caller has received on their listings, newest first.</summary>
    public async Task<OfferViewPage> ListReceivedAsync(string? cursor, CancellationToken ct)
    {
        var viewerId = currentUser.RequireUserId();
        var page = await offers.ListForSellerAsync(viewerId, cursor, PageSize, ct).ConfigureAwait(false);
        return await BuildPageAsync(page, viewerId, ct).ConfigureAwait(false);
    }

    /// <summary>One offer, or <c>null</c> when it does not exist or the caller is not on either side of it.</summary>
    public async Task<OfferView?> GetAsync(string offerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var viewerId = currentUser.RequireUserId();
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);

        // Not found and not yours look the same from outside, so an offer id cannot be probed.
        if (offer is null || offer.PartyOf(viewerId) is null)
        {
            return null;
        }

        offer = await OfferExpiry.ApplyAsync(offers, offer, clock, ct).ConfigureAwait(false);
        var views = await BuildAsync([offer], viewerId, ct).ConfigureAwait(false);
        return views.Count > 0 ? views[0] : null;
    }

    private async Task<OfferViewPage> BuildPageAsync(Page<Offer> page, string viewerId, CancellationToken ct)
    {
        var current = new List<Offer>(page.Items.Count);
        foreach (var offer in page.Items)
        {
            current.Add(await OfferExpiry.ApplyAsync(offers, offer, clock, ct).ConfigureAwait(false));
        }

        return new OfferViewPage(await BuildAsync(current, viewerId, ct).ConfigureAwait(false), page.NextCursor);
    }

    private async Task<List<OfferView>> BuildAsync(List<Offer> source, string viewerId, CancellationToken ct)
    {
        var listingCache = new Dictionary<string, (Listing? Listing, string? CategorySlug)>(StringComparer.Ordinal);
        var views = new List<OfferView>(source.Count);

        foreach (var offer in source)
        {
            if (offer.PartyOf(viewerId) is not { } role)
            {
                continue;
            }

            if (!listingCache.TryGetValue(offer.ListingId, out var found))
            {
                var listing = await listings.FindAsync(offer.ListingId, ct).ConfigureAwait(false);
                var category = listing is null
                    ? null
                    : await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);
                found = (listing, category?.Slug);
                listingCache[offer.ListingId] = found;
            }

            var status = offer.Status;

            // A checked-out offer whose order was never paid and has died is the buyer's deal
            // again (see Offer.ReopenAfterUnpaidOrder). Shown that way here so they can resume;
            // the checkout itself makes it official.
            if (status == OfferStatus.CheckedOut && role == OfferParty.Buyer
                && await OrderIsDeadAsync(offer, ct).ConfigureAwait(false))
            {
                status = clock.UtcNow >= offer.ExpiresAt ? OfferStatus.Expired : OfferStatus.Accepted;
            }

            // Named by public handle, resolved from the account ids at read time. The sign-in names
            // stored on the offer are emails and must never reach a response.
            var buyerHandle = (await identities.ResolveAsync(offer.BuyerId, ct).ConfigureAwait(false)).Handle;
            var sellerHandle = (await identities.ResolveAsync(offer.SellerId, ct).ConfigureAwait(false)).Handle;

            var canCheckout = status == OfferStatus.Accepted
                && role == OfferParty.Buyer
                && found.Listing is { Status: ListingStatus.Active };

            views.Add(new OfferView(
                offer,
                role,
                status,
                found.Listing?.Title,
                found.Listing is { Photos.Count: > 0 } withPhoto ? withPhoto.Photos[0].StorageKey : null,
                found.CategorySlug,
                // Only the buyer has an order to look at; the seller finds it in their own orders.
                role == OfferParty.Buyer ? offer.OrderId : null,
                canCheckout,
                buyerHandle,
                sellerHandle));
        }

        return views;
    }

    private Task<bool> OrderIsDeadAsync(Offer offer, CancellationToken ct) =>
        OfferOrders.IsDeadAsync(orders, offer, clock, ct);
}

/// <summary>Whether the order an offer produced can no longer be paid.</summary>
internal static class OfferOrders
{
    /// <summary>
    /// <c>true</c> when the order is missing, cancelled, or unpaid past its payment window:
    /// nothing is left to pay for, so the offer's deal is still open.
    /// </summary>
    public static async Task<bool> IsDeadAsync(IOrderRepository orders, Offer offer, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(offer.OrderId))
        {
            return true;
        }

        var order = await orders.FindAsync(offer.OrderId, ct).ConfigureAwait(false);
        return order is null || order.EscrowState == EscrowState.Cancelled || order.HasPaymentExpired(clock);
    }
}

/// <summary>An offer as one of its two parties sees it.</summary>
/// <param name="Offer">The offer, with any lapsed deadline already applied.</param>
/// <param name="ViewerRole">Which side the viewer is on.</param>
/// <param name="Status">
/// What to show: the stored status, except that a checked-out offer whose order died reads as
/// accepted again for its buyer.
/// </param>
/// <param name="ListingTitle"><c>null</c> if the listing has been removed.</param>
/// <param name="ListingPhotoKey">The listing's first photo, as a base storage key.</param>
/// <param name="CategorySlug">For the link to the listing.</param>
/// <param name="OrderId">The buyer's own order, once checked out. Never given to the seller.</param>
/// <param name="CanCheckout">The buyer can pay for it right now: accepted, in time, and still for sale.</param>
public sealed record OfferView(
    Offer Offer,
    OfferParty ViewerRole,
    OfferStatus Status,
    string? ListingTitle,
    string? ListingPhotoKey,
    string? CategorySlug,
    string? OrderId,
    bool CanCheckout,
    string BuyerHandle,
    string SellerHandle);

public sealed record OfferViewPage(IReadOnlyList<OfferView> Items, string? NextCursor);
