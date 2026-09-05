using Koleksi.Application.Abstractions;
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
    ICurrentUser currentUser)
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
            var sellerUsername = await listings.FindSellerUsernameAsync(listing.Id, ct).ConfigureAwait(false);

            rows.Add(new CartRowView(
                listing.Id,
                listing.Title,
                category?.Slug,
                listing.Price.Rupiah,
                listing.Condition,
                listing.SellerId,
                sellerUsername,
                listing.City,
                listing.Photos.Count > 0 ? media.PublicUrl(listing.Photos[0].StorageKey) : null,
                entry.AddedAt));
        }

        return new CartView(rows);
    }
}

/// <param name="CategorySlug"><c>null</c> only if the category was since removed — the listing itself still exists.</param>
/// <param name="SellerUsername">
/// <c>null</c> when the listing was never indexed into browse — see
/// <c>IListingRepository.FindSellerUsernameAsync</c>. Cannot actually happen for a row that
/// just passed the <c>Active</c> check above, since publishing is what indexes a listing,
/// but the type stays honest about it rather than asserting a guarantee this handler does
/// not itself enforce.
/// </param>
public sealed record CartRowView(
    string ListingId,
    string Title,
    string? CategorySlug,
    long PriceRupiah,
    string Condition,
    string SellerId,
    string? SellerUsername,
    string SellerCity,
    string? PhotoUrl,
    DateTimeOffset AddedAt);

public sealed record CartView(IReadOnlyList<CartRowView> Items);
