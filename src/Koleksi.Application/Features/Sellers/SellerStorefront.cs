using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Reviews;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Features.Sellers;

/// <summary>
/// Looks a storefront up by its public handle. Every storefront read starts here, so a bad or
/// unknown handle is refused identically everywhere.
/// </summary>
internal static class StorefrontLookup
{
    public static async Task<PublicProfile?> FindAsync(IPublicProfileRepository profiles, string? handle, CancellationToken ct)
    {
        // A URL segment, so anything that is not a well-formed handle is simply "no such shop".
        // Reserved words are rejected by the same normaliser; none of them can be owned.
        if (PublicHandle.TryNormalize(handle, out var canonical) is not null)
        {
            return null;
        }

        return await profiles.FindByHandleAsync(canonical, ct).ConfigureAwait(false);
    }
}

/// <summary>The public header of a seller's storefront.</summary>
public sealed class GetSellerStorefrontHandler(
    IPublicProfileRepository publicProfiles,
    IProfileRepository accounts,
    IListingRepository listings,
    IReviewRepository reviews,
    IMediaStorage media)
{
    public async Task<SellerStorefrontView?> HandleAsync(string? handle, CancellationToken ct)
    {
        var profile = await StorefrontLookup.FindAsync(publicProfiles, handle, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        // Four independent reads; issued together so the page costs one round trip of latency.
        var accountTask = accounts.FindAsync(profile.UserId, ct);
        var activeTask = listings.CountBySellerAsync(profile.UserId, ListingStatus.Active, ct);
        var soldTask = listings.CountBySellerAsync(profile.UserId, ListingStatus.Sold, ct);
        var ratingTask = reviews.GetStatsAsync(profile.UserId, ct);
        await Task.WhenAll(accountTask, activeTask, soldTask, ratingTask).ConfigureAwait(false);

        var account = await accountTask.ConfigureAwait(false);

        return new SellerStorefrontView(
            profile.Handle,
            profile.DisplayName,
            profile.MemberSince,

            // The seller chose this picture as their avatar; it is shown as that and nothing more.
            account?.AvatarKey is { } avatar ? media.PublicUrl(avatar) : null,

            // Only facts that are real. PhoneVerified alone is not one: completing the email-code
            // fallback also sets it, and that proves an inbox, not a phone (QA D-10). A recorded
            // number is only ever stored by completing the phone code check, so require both.
            // Identity verification is deliberately not reported (nothing sets it to verified).
            ContactVerified: account is { PhoneVerified: true, Phone: { Length: > 0 } },
            ActiveListingCount: await activeTask.ConfigureAwait(false),
            SoldCount: await soldTask.ConfigureAwait(false),
            Rating: await ratingTask.ConfigureAwait(false));
    }
}

/// <param name="Handle">The public handle; never the sign-in name.</param>
/// <param name="DisplayName">The name the seller chose to show, or <c>null</c> to show the handle.</param>
/// <param name="ContactVerified">The seller completed a one-time-code check of their phone or email.</param>
public sealed record SellerStorefrontView(
    string Handle,
    string? DisplayName,
    DateTimeOffset MemberSince,
    string? AvatarUrl,
    bool ContactVerified,
    int ActiveListingCount,
    int SoldCount,
    SellerRatingStats Rating);

/// <summary>Which of a seller's live listings a storefront tab wants.</summary>
public enum StorefrontListingKind
{
    /// <summary>Fixed-price and accept-offers listings.</summary>
    Items = 0,

    /// <summary>Timed auctions.</summary>
    Auctions = 1,
}

/// <summary>A page of a seller's live listings, as browse cards.</summary>
public sealed class ListSellerListingsHandler(
    IPublicProfileRepository publicProfiles,
    IListingRepository listings,
    IAuctionRepository auctions,
    ICategoryRepository categories)
{
    /// <summary>Listings read from the seller index per round trip.</summary>
    private const int PageSize = 24;

    /// <summary>
    /// How many index pages one request may walk looking for a kind that is rare in the seller's
    /// catalogue. A cursor is returned, so nothing is lost by stopping.
    /// </summary>
    private const int MaxPagesPerRequest = 4;

    public async Task<SellerListingsPage?> HandleAsync(
        string? handle, StorefrontListingKind kind, string? cursor, CancellationToken ct)
    {
        var profile = await StorefrontLookup.FindAsync(publicProfiles, handle, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var wantAuctions = kind == StorefrontListingKind.Auctions;
        var cards = new List<ListingSummary>();
        var categorySlugs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? next = cursor;

        for (var round = 0; round < MaxPagesPerRequest; round++)
        {
            var page = await listings
                .ListBySellerAsync(profile.UserId, ListingStatus.Active, next, PageSize, ct).ConfigureAwait(false);

            foreach (var listing in page.Items.Where(l => (l.Format == ListingFormat.Auction) == wantAuctions))
            {
                if (!categorySlugs.TryGetValue(listing.CategoryId, out var slug))
                {
                    slug = (await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false))?.Slug ?? string.Empty;
                    categorySlugs[listing.CategoryId] = slug;
                }

                var card = await ToCardAsync(listing, slug, profile, ct).ConfigureAwait(false);
                if (card is not null)
                {
                    cards.Add(card);
                }
            }

            next = page.NextCursor;
            if (next is null || cards.Count >= PageSize / 2)
            {
                break;
            }
        }

        return new SellerListingsPage(cards, next);
    }

    private async Task<ListingSummary?> ToCardAsync(
        Listing listing, string categorySlug, PublicProfile seller, CancellationToken ct)
    {
        var price = listing.Price.Rupiah;
        DateTimeOffset? endsAt = null;
        var bids = 0;

        if (listing.Format == ListingFormat.Auction)
        {
            // An auction listing without a live auction has nothing to bid on, so it is not shown
            // as one (a "live auctions" tab must only hold auctions with a real end time).
            var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
            if (auction is not { Status: AuctionStatus.Live })
            {
                return null;
            }

            price = auction.CurrentPrice.Rupiah;
            endsAt = auction.EndsAt;
            bids = auction.BidCount;
        }

        return new ListingSummary(
            listing.Id,
            listing.Title,
            listing.CategoryId,
            categorySlug,
            price,
            listing.Format,
            listing.Status,
            listing.Condition,
            seller.Handle,
            listing.City,
            listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
            endsAt,
            bids,
            seller.UserId);
    }
}

public sealed record SellerListingsPage(IReadOnlyList<ListingSummary> Items, string? NextCursor);

/// <summary>A page of a seller's reviews, newest first, with the buyer's identity resolved or withheld.</summary>
public sealed class ListSellerReviewsHandler(
    IPublicProfileRepository publicProfiles,
    IReviewRepository reviews,
    PublicIdentityResolver identities)
{
    public const int DefaultLimit = 10;

    public const int MaxLimit = 30;

    public async Task<SellerReviewsPage?> HandleAsync(string? handle, string? cursor, int? limit, CancellationToken ct)
    {
        var profile = await StorefrontLookup.FindAsync(publicProfiles, handle, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        var page = await reviews
            .ListBySellerAsync(profile.UserId, cursor, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct)
            .ConfigureAwait(false);

        // Only the named reviewers are looked up; an anonymous review never touches the resolver,
        // so nothing about its author can reach the response by accident.
        var names = await identities
            .ResolveManyAsync(page.Items.Where(r => !r.IsAnonymous).Select(r => r.BuyerId), ct)
            .ConfigureAwait(false);

        var items = page.Items.Select(review => new ReviewView(
            review.Id,
            review.Rating,
            review.Text,
            review.CreatedAt,
            review.IsAnonymous,
            review.IsAnonymous ? null : names[review.BuyerId].ShownName)).ToList();

        return new SellerReviewsPage(items, page.NextCursor);
    }
}

/// <param name="ReviewerName">The reviewer's shown name, or <c>null</c> for an anonymous review.</param>
public sealed record ReviewView(
    string Id,
    int Rating,
    string? Text,
    DateTimeOffset CreatedAt,
    bool IsAnonymous,
    string? ReviewerName);

public sealed record SellerReviewsPage(IReadOnlyList<ReviewView> Items, string? NextCursor);
