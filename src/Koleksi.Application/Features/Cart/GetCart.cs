using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;

namespace Koleksi.Application.Features.Cart;

/// <summary>
/// Serves the cart screen: every listing the buyer added, read live rather than from a
/// price frozen at the moment it was added.
/// </summary>
/// <remarks>
/// A cart entry whose listing has since sold, been archived, been suspended, or somehow
/// become an auction is dropped from the response and swept out of storage in the same
/// pass — the same "a stale pointer cleans itself up on next read" approach the rest of
/// this codebase uses rather than a background job.
/// </remarks>
public sealed class GetCartHandler(
    ICartRepository cart,
    IListingRepository listings,
    ICategoryRepository categories,
    IMediaStorage media,
    IListingHoldStore holds,
    ICurrentUser currentUser,
    PublicIdentityResolver identities)
{
    public async Task<CartView> HandleAsync(CancellationToken ct)
    {
        var buyerId = currentUser.RequireUserId();
        var entries = await cart.ListAsync(buyerId, ct).ConfigureAwait(false);

        var rows = new List<CartRowView>(entries.Count);
        foreach (var entry in entries)
        {
            var listing = await listings.FindAsync(entry.ListingId, ct).ConfigureAwait(false);
            if (listing is null || listing.Status != ListingStatus.Active || listing.Format == ListingFormat.Auction)
            {
                await cart.RemoveItemAsync(buyerId, entry.ListingId, ct).ConfigureAwait(false);
                continue;
            }

            var category = await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);
            var shopHandle = (await identities.ResolveAsync(listing.SellerId, ct).ConfigureAwait(false)).Handle;

            // Units a buyer could reserve right now, so the cart can say "only 2 left" or "being paid by
            // someone else" before the buyer presses checkout. The buyer's own unpaid hold counts as taken.
            var availability = await holds.GetAvailabilityAsync(listing.Id, listing.Stock, ct).ConfigureAwait(false);

            rows.Add(new CartRowView(
                listing.Id,
                listing.Title,
                category?.Slug,
                listing.Price.Rupiah,
                listing.Condition,
                shopHandle,
                shopHandle,
                listing.City,
                listing.Photos.Count > 0 ? media.PublicUrl(listing.Photos[0].StorageKey, PhotoSize.Small) : null,
                entry.AddedAt,
                Quantity: Math.Min(entry.Quantity, listing.Stock),
                Stock: listing.Stock,
                Available: availability.Available));
        }

        return new CartView(rows);
    }
}

/// <param name="CategorySlug"><c>null</c> only if the category was since removed — the listing itself still exists.</param>
/// <param name="ShopHandle">The seller's public handle: the key a checkout names a shop by. The account id is never exposed.</param>
/// <param name="SellerUsername">
/// The seller's public handle (the field keeps its name for the frontend). Never the sign-in
/// name, which for an email account is the email address.
/// </param>
public sealed record CartRowView(
    string ListingId,
    string Title,
    string? CategorySlug,
    long PriceRupiah,
    string Condition,
    string ShopHandle,
    string? SellerUsername,
    string SellerCity,
    string? PhotoUrl,
    DateTimeOffset AddedAt,
    int Quantity = 1,
    int Stock = 1,
    int Available = 1);

public sealed record CartView(IReadOnlyList<CartRowView> Items);
