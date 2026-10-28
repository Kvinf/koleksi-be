using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Turns a stored photo key into the URL a browser can load, so no client has to know where
/// the media lives or how a key maps onto it.
/// </summary>
/// <remarks>
/// A browser used to rebuild this URL from a build-time variable, and a build made without that
/// variable showed grey placeholders on every card while the item page, which uses the URL the
/// API sends, kept working. The API is the one place that already knows the CDN base and the
/// legacy single-object keys, so it says the URL and the page uses it as it is.
/// <para>
/// The URL is always the <c>original</c> rendition. A listing photo is three objects under one key
/// (<c>.../original</c>, <c>.../large</c>, <c>.../small</c>), and the page swaps the last segment for
/// the size it wants (<c>sizedPhotoUrl</c> in the frontend). A legacy key is one object with no size
/// segment, so its URL has nothing to swap and every size resolves to that object.
/// </para>
/// </remarks>
internal static class PhotoUrls
{
    /// <summary>The public URL of a photo's original rendition, or <c>null</c> when there is no photo.</summary>
    public static string? OriginalUrl(this IMediaStorage media, string? storageKey)
    {
        ArgumentNullException.ThrowIfNull(media);

        return string.IsNullOrWhiteSpace(storageKey) ? null : media.PublicUrl(storageKey, PhotoSize.Original);
    }

    /// <summary>A listing summary as it goes to a client: the same fields, plus the photo's URL.</summary>
    public static ListingSummaryResponse ToResponse(this ListingSummary summary, IMediaStorage media)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ListingSummaryResponse(
            summary.Id,
            summary.Title,
            summary.CategoryId,
            summary.CategorySlug,
            summary.PriceRupiah,
            summary.Format,
            summary.Status,
            summary.Condition,
            summary.SellerUsername,
            summary.City,
            summary.PrimaryPhotoKey,
            media.OriginalUrl(summary.PrimaryPhotoKey),
            summary.AuctionEndsAt,
            summary.BidCount);
    }
}

/// <summary>The wire shape of a listing card. The seller's id never appears.</summary>
internal sealed record ListingSummaryResponse(
    string Id,
    string Title,
    string CategoryId,
    string CategorySlug,
    long PriceRupiah,
    ListingFormat Format,
    ListingStatus Status,
    string Condition,
    string SellerUsername,
    string City,
    string? PrimaryPhotoKey,
    string? PrimaryPhotoUrl,
    DateTimeOffset? AuctionEndsAt,
    int BidCount);
