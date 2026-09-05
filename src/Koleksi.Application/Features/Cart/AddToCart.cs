using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Cart;

/// <summary>Adds a listing to the authenticated caller's cart.</summary>
/// <remarks>
/// Naturally idempotent — adding a listing already in the cart just refreshes its
/// <c>addedAt</c> — so, unlike a call that mints a new resource, this is not wrapped in
/// <see cref="IIdempotencyStore"/>.
/// </remarks>
public sealed class AddToCartHandler(
    ICartRepository cart,
    IListingRepository listings,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>
    /// A hard ceiling on cart size.
    /// </summary>
    /// <remarks>
    /// Without one, a cart could grow without bound, and every future cart read pays for
    /// it. Fifty is far beyond what a shopper buying across several sellers in one
    /// checkout would ever need.
    /// </remarks>
    public const int MaxItems = 50;

    public async Task<AddToCartResult> HandleAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var buyerId = currentUser.RequireUserId();

        var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);
        if (listing is null || listing.Status != ListingStatus.Active || listing.Format == ListingFormat.Auction)
        {
            // Same coarse answer for "does not exist", "not published", and "is an
            // auction" — an auction is won by bidding, never added to a cart, and none of
            // these distinctions change what the caller should do.
            return AddToCartResult.NotFound;
        }

        if (string.Equals(listing.SellerId, buyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("cart.own_listing", "You cannot add your own listing to your cart.");
        }

        var existing = await cart.ListAsync(buyerId, ct).ConfigureAwait(false);
        if (existing.Count >= MaxItems && !existing.Any(item => item.ListingId == listing.Id))
        {
            throw DomainException.For("cart.limit_reached", $"A cart can hold at most {MaxItems} items.");
        }

        await cart.AddItemAsync(buyerId, listing.Id, clock.UtcNow, ct).ConfigureAwait(false);
        return AddToCartResult.Added;
    }
}

/// <param name="Found"><c>false</c> when the listing does not exist, is not active, or is an auction.</param>
public sealed record AddToCartResult(bool Found)
{
    public static AddToCartResult NotFound { get; } = new(false);

    public static AddToCartResult Added { get; } = new(true);
}
