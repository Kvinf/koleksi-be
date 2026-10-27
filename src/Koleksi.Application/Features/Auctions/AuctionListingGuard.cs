using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Auctions;

/// <summary>
/// Bidding checks the auction, not the listing, so the listing has to be checked here: an auction
/// whose listing is archived, sold or suspended must not keep taking bids or buyouts.
/// </summary>
internal static class AuctionListingGuard
{
    /// <returns><c>false</c> when the listing does not exist at all.</returns>
    /// <exception cref="DomainException"><c>auction.listing_unavailable</c>: the listing exists but is not for sale.</exception>
    public static async Task<bool> EnsureForSaleAsync(IListingRepository listings, string listingId, CancellationToken ct)
    {
        var listing = await listings.FindAsync(listingId, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return false;
        }

        if (listing.Status != ListingStatus.Active)
        {
            throw DomainException.For("auction.listing_unavailable", "This listing is no longer for sale.");
        }

        return true;
    }
}
