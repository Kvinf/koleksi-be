using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Catalog;

/// <summary>
/// Serves the item page, and the auction page when the listing is a <c>lelang</c>.
/// </summary>
/// <remarks>
/// <para>
/// This handler decides what an anonymous visitor may see, so it is the natural place for
/// the visibility rules rather than a filter somewhere upstream. A draft, archived, or
/// suspended listing is not visible to the public — and a suspended one is not visible to
/// its own seller either, only to staff, because a seller who can still see their removed
/// listing will keep linking to it.
/// </para>
/// <para>
/// Bidder handles are masked here, at the read boundary, not in the view. Bid history is
/// public on every auction page; an unmasked history would publish who is spending what
/// and make shill rings trivial to coordinate.
/// </para>
/// </remarks>
public sealed class GetListingHandler(
    IListingRepository listings,
    IAuctionRepository auctions,
    ICategoryRepository categories,
    IMediaStorage media,
    IListingHoldStore holds,
    ICurrentUser currentUser,
    IClock clock,
    PublicIdentityResolver identities)
{
    /// <summary>How many history rows the auction page shows.</summary>
    private const int BidHistoryLimit = 25;

    public async Task<ListingDetailResult> HandleAsync(GetListingQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The URL carries "{slug}-{id}". Anything that is not a well-formed id is a
        // fabricated URL, and is refused before it reaches storage — which is also what
        // stops a traversal-shaped path from ever becoming a key.
        var listingId = Slug.ExtractId(query.SlugWithId);
        if (listingId is null)
        {
            return ListingDetailResult.NotFound;
        }

        var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);
        if (listing is null || !IsVisibleTo(listing))
        {
            // Deliberately the same answer for "does not exist" and "you may not see it".
            // Distinguishing them turns the endpoint into an oracle for which listings a
            // moderator has removed.
            return ListingDetailResult.NotFound;
        }

        var category = await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);

        // The seller as the public knows them: a handle and an optional display name, never the
        // sign-in name stored on older rows (which for an email account is the email address).
        var seller = await identities.ResolveAsync(listing.SellerId, ct).ConfigureAwait(false);

        AuctionView? auctionView = null;
        if (listing.Format == ListingFormat.Auction)
        {
            var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
            if (auction is not null)
            {
                // Only this round's bids: a lot relisted after it ended unsold (or unpaid) runs a fresh
                // auction over the same listing id, and the earlier round's bid rows are still stored.
                var history = (await auctions
                    .GetBidHistoryAsync(listing.Id, BidHistoryLimit, ct)
                    .ConfigureAwait(false))
                    .Where(bid => bid.PlacedAt >= auction.StartsAt)
                    .ToList();

                // Bidders are named by their current handle, masked below. The name stored on the
                // bid was the sign-in name at the time and is not trusted for display.
                var bidders = await identities
                    .ResolveManyAsync(history.Select(bid => bid.BidderId), ct).ConfigureAwait(false);

                auctionView = BuildAuctionView(auction, history, bidders);
            }
        }

        var photos = new List<PhotoView>(listing.Photos.Count);
        foreach (var photo in listing.Photos)
        {
            // The domain stores keys, never URLs. The public URL is composed here, from our
            // own CDN base, so a listing can never point a browser at a host we do not own.
            // Original, explicitly: this is the detail page's own hero image, the one spot
            // the full-resolution upload is actually worth its weight.
            photos.Add(new PhotoView(media.PublicUrl(photo.StorageKey, PhotoSize.Original), photo.Width, photo.Height));
        }

        // How many units a buyer could take right now. A listing that is not for sale (a draft, an archived
        // one) has nothing reserved, so it reads as its full stock.
        StockView? stockView = null;
        if (listing.Format != ListingFormat.Auction)
        {
            var usage = await holds.GetAvailabilityAsync(listing.Id, listing.Stock, ct).ConfigureAwait(false);
            stockView = new StockView(listing.Stock, usage.Available, usage.Sold, usage.Held);
        }

        return new ListingDetailResult(
            Found: true,
            Listing: listing,
            Category: category,
            Photos: photos,
            Auction: auctionView,
            CanonicalPath: category is null ? null : listing.UrlPath(category.Slug),
            IsOwnListing: currentUser.IsAuthenticated
                && string.Equals(listing.SellerId, currentUser.UserId, StringComparison.Ordinal),
            Seller: new PublicSellerView(seller.Handle, seller.DisplayName),
            Stock: stockView);
    }

    private bool IsVisibleTo(Listing listing)
    {
        if (listing.IsPubliclyVisible)
        {
            return true;
        }

        if (currentUser.IsInRole("staff"))
        {
            return true;
        }

        // A seller may see their own drafts and archived listings, but not a suspension —
        // that is a moderation decision they cannot browse around.
        return listing.Status != ListingStatus.Suspended
            && currentUser.IsAuthenticated
            && string.Equals(listing.SellerId, currentUser.UserId, StringComparison.Ordinal);
    }

    private AuctionView BuildAuctionView(
        Auction auction, List<Bid> history, IReadOnlyDictionary<string, Koleksi.Domain.Users.PublicProfile> bidders)
    {
        var rows = new List<BidRowView>(history.Count);
        foreach (var bid in history)
        {
            rows.Add(new BidRowView(
                // Masked at the boundary. The unmasked handle never enters the result.
                BidderAlias.Mask(bidders[bid.BidderId].Handle),
                bid.Amount.Rupiah,
                bid.PlacedAt,
                IsMine: currentUser.IsAuthenticated
                    && string.Equals(bid.BidderId, currentUser.UserId, StringComparison.Ordinal)));
        }

        var viewerId = currentUser.UserId;

        return new AuctionView(
            Status: auction.Status,
            CurrentPriceRupiah: auction.CurrentPrice.Rupiah,
            NextMinimumBidRupiah: auction.NextMinimumBid.Rupiah,
            IncrementRupiah: auction.MinimumIncrement.Rupiah,
            BidCount: auction.BidCount,
            EndsAt: auction.EndsAt,
            IsEndingSoon: auction.IsEndingSoon(clock),
            AntiSnipeWindowSeconds: (int)Auction.AntiSnipeWindow.TotalSeconds,

            // Both shown openly — see Auction.ReservePrice/BuyoutPrice's own remarks.
            ReservePriceRupiah: auction.ReservePrice?.Rupiah,
            ReserveMet: auction.ReserveMet,
            BuyoutPriceRupiah: auction.BuyoutPrice?.Rupiah,

            // The design shows "You are the highest bidder" only to the leader. Whether
            // the viewer leads is a boolean; the leader's identity stays on the server.
            ViewerIsLeading: viewerId is not null
                && string.Equals(auction.LeadingBidderId, viewerId, StringComparison.Ordinal),

            ViewerHasBid: rows.Exists(row => row.IsMine),
            History: rows,

            // Only the winner is told when the time to pay runs out; nobody else has a use for it.
            PaymentDueAt: viewerId is not null
                && auction.Status == AuctionStatus.Ended
                && string.Equals(auction.LeadingBidderId, viewerId, StringComparison.Ordinal)
                ? auction.PaymentDueAt
                : null);
    }
}

/// <param name="SlugWithId">The final URL segment, e.g. <c>psa-10-charizard-01J8Z...</c>.</param>
public sealed record GetListingQuery(string SlugWithId);

/// <summary>Everything the item and auction pages render.</summary>
/// <param name="Found"><c>false</c> when the listing does not exist or is not visible to the caller.</param>
/// <param name="CanonicalPath">
/// The one true path for this listing. Emitted as <c>rel="canonical"</c> so a stale slug
/// in an inbound link does not split the page's search ranking across two URLs.
/// </param>
public sealed record ListingDetailResult(
    bool Found,
    Listing? Listing,
    Category? Category,
    IReadOnlyList<PhotoView> Photos,
    AuctionView? Auction,
    string? CanonicalPath,
    bool IsOwnListing,
    PublicSellerView? Seller = null,
    StockView? Stock = null)
{
    public static ListingDetailResult NotFound { get; } =
        new(false, null, null, [], null, null, false);
}

/// <summary>How many units a listing has, and how many a buyer could reserve right now.</summary>
/// <param name="Total">Units the seller listed.</param>
/// <param name="Available">Free to reserve now. Zero with <paramref name="Held"/> above zero means "being paid for; may come back".</param>
/// <param name="Sold">Units already paid for.</param>
/// <param name="Held">Units reserved by unpaid orders whose time has not run out.</param>
public sealed record StockView(int Total, int Available, int Sold, int Held);

/// <summary>The seller as shown on a listing: the handle that links to their storefront, and the name they chose to show.</summary>
public sealed record PublicSellerView(string Handle, string? DisplayName);

/// <param name="Url">Absolute CDN URL, composed from our own base and the stored key.</param>
public sealed record PhotoView(string Url, int Width, int Height);

/// <summary>The bidding half of an auction page. Carries no bidder identity and no maximum.</summary>
/// <param name="ReservePriceRupiah">
/// The lowest winning price the seller will accept, shown openly. <c>null</c> means any
/// bid wins.
/// </param>
/// <param name="ReserveMet">Whether <see cref="CurrentPriceRupiah"/> has reached the reserve.</param>
/// <param name="BuyoutPriceRupiah">
/// A fixed price that ends the auction immediately as a sale, at any point in bidding.
/// <c>null</c> means there is no buyout.
/// </param>
/// <param name="PaymentDueAt">
/// When the winner must have paid by, shown only to the winner of an ended auction; <c>null</c> for everyone else.
/// </param>
public sealed record AuctionView(
    AuctionStatus Status,
    long CurrentPriceRupiah,
    long NextMinimumBidRupiah,
    long IncrementRupiah,
    int BidCount,
    DateTimeOffset EndsAt,
    bool IsEndingSoon,
    int AntiSnipeWindowSeconds,
    bool ViewerIsLeading,
    bool ViewerHasBid,
    IReadOnlyList<BidRowView> History,
    long? ReservePriceRupiah,
    bool ReserveMet,
    long? BuyoutPriceRupiah,
    DateTimeOffset? PaymentDueAt = null);

/// <param name="MaskedBidder">The handle as <c>ran•••hw</c>. The real one never leaves the server.</param>
/// <param name="IsMine">Lets the design highlight the viewer's own rows without naming anyone.</param>
public sealed record BidRowView(
    string MaskedBidder,
    long AmountRupiah,
    DateTimeOffset PlacedAt,
    bool IsMine);
