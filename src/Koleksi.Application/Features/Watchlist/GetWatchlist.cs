using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Catalog;

namespace Koleksi.Application.Features.Watchlist;

/// <summary>Just the ids the caller has saved, for filling in hearts.</summary>
/// <remarks>
/// One query on the caller's own partition and nothing else, so the browser can ask once per
/// visit and mark every card on every page without a read per card.
/// </remarks>
public sealed class GetWatchedIdsHandler(IWatchlistRepository watchlist, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<string>> HandleAsync(CancellationToken ct)
    {
        var entries = await watchlist.ListAsync(currentUser.RequireUserId(), ct).ConfigureAwait(false);
        return entries.Select(entry => entry.ListingId).ToList();
    }
}

/// <summary>
/// Serves the saved-items page: the caller's saved listings as cards, newest saved first.
/// </summary>
/// <remarks>
/// <para>
/// Cards are built from the live listing, not from anything stored with the save, so a price
/// change shows at once and a listing that has since sold comes back with its status
/// (<c>Sold</c>, <c>Archived</c>) for the page to draw as unavailable rather than vanishing.
/// </para>
/// <para>
/// Two kinds of entry are not returned. A listing that no longer exists is removed from the
/// caller's list as it is found (the same self-cleaning read the cart uses). A draft or
/// suspended one is only hidden: it is not public, and a seller may yet republish it. Both mean
/// a page can hold fewer cards than its size, and <see cref="WatchlistPage.Total"/> is the
/// number of saves rather than of cards drawn.
/// </para>
/// </remarks>
public sealed class GetWatchlistHandler(
    IWatchlistRepository watchlist,
    IListingRepository listings,
    IAuctionRepository auctions,
    ICategoryRepository categories,
    PublicIdentityResolver identities,
    ICurrentUser currentUser)
{
    public const int DefaultPageSize = 24;

    public const int MaxPageSize = 48;

    public async Task<WatchlistPage> HandleAsync(int pageNumber, int pageSize, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var entries = await watchlist.ListAsync(userId, ct).ConfigureAwait(false);

        // Clamped rather than refused, like browse: a stale "?page=9" lands on the last page.
        var lastPage = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)size));
        var page = Math.Clamp(pageNumber, 1, lastPage);

        var window = entries.Skip((page - 1) * size).Take(size).ToList();
        var cards = await Task.WhenAll(window.Select(entry => ResolveAsync(userId, entry, ct))).ConfigureAwait(false);

        return new WatchlistPage(
            cards.Where(card => card is not null).Select(card => card!).ToList(),
            page,
            size,
            entries.Count,
            HasNext: page < lastPage);
    }

    private async Task<SavedListingView?> ResolveAsync(string userId, WatchEntry entry, CancellationToken ct)
    {
        var listing = await listings.FindAsync(entry.ListingId, ct).ConfigureAwait(false);
        if (listing is null)
        {
            await watchlist.RemoveAsync(userId, entry.ListingId, ct).ConfigureAwait(false);
            return null;
        }

        if (listing.Status is ListingStatus.Draft or ListingStatus.Suspended)
        {
            return null;
        }

        var category = await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);
        if (category is null)
        {
            return null;
        }

        // The seller's public handle, resolved from the account id at read time like the cart and
        // browse do. The stored sign-in name (an email) must never reach a response.
        var sellerUsername = (await identities.ResolveAsync(listing.SellerId, ct).ConfigureAwait(false)).Handle;

        var price = listing.Price.Rupiah;
        DateTimeOffset? auctionEndsAt = null;
        var bidCount = 0;
        if (listing.Format == ListingFormat.Auction)
        {
            var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
            if (auction is not null)
            {
                price = auction.CurrentPrice.Rupiah;
                auctionEndsAt = auction.EndsAt;
                bidCount = auction.BidCount;
            }
        }

        return new SavedListingView(
            new ListingSummary(
                listing.Id,
                listing.Title,
                listing.CategoryId,
                category.Slug,
                price,
                listing.Format,
                listing.Status,
                listing.Condition,
                sellerUsername,
                listing.City,
                listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                auctionEndsAt,
                bidCount),
            entry.SavedAt);
    }
}

/// <summary>One saved listing as the same card the browse grid draws, plus when it was saved.</summary>
public sealed record SavedListingView(ListingSummary Listing, DateTimeOffset SavedAt);

public sealed record WatchlistPage(
    IReadOnlyList<SavedListingView> Items,
    int PageNumber,
    int PageSize,
    int Total,
    bool HasNext);
