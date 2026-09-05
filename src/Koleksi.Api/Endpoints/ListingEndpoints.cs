using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Listings;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// A seller's own listings: create, publish, archive, and the dashboard's read of them.
/// </summary>
/// <remarks>
/// No <c>AllowAnonymous</c> anywhere in this group — the fallback authorization policy
/// already requires a signed-in caller for everything here, which is correct: there is no
/// public write in this product, and "my listings" is meaningless without an owner.
/// </remarks>
internal static class ListingEndpoints
{
    public static IEndpointRouteBuilder MapListingEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/listings").WithTags("Listings");

        group.MapPost("", CreateListingAsync).WithName("CreateListing");
        group.MapPost("/{id}/publish", PublishListingAsync).WithName("PublishListing");
        group.MapPost("/{id}/archive", ArchiveListingAsync).WithName("ArchiveListing");
        group.MapGet("/mine", GetMyListingsAsync).WithName("GetMyListings");

        return app;
    }

    private static async Task<IResult> CreateListingAsync(
        CreateListingRequest request,
        CreateListingHandler handler,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CategoryId)
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.Condition)
            || string.IsNullOrWhiteSpace(request.City))
        {
            return Results.BadRequest(new { code = "listing.invalid_details" });
        }

        if (!Enum.TryParse<ListingFormat>(request.Format, ignoreCase: false, out var format))
        {
            return Results.BadRequest(new { code = "listing.invalid_format" });
        }

        var command = new CreateListingCommand(
            request.CategoryId,
            request.Title,
            request.PriceRupiah,
            format,
            request.Condition,
            request.City,
            request.Description ?? string.Empty,
            (request.Photos ?? []).Select(p => new PhotoInput(p.StorageKey, p.Width, p.Height)).ToList(),
            request.AutoDeclineBelowPercent,
            request.AuctionDurationDays,
            request.ReservePriceRupiah,
            request.BuyoutPriceRupiah,
            request.Publish);

        // A double-tap on "Publish" — or a browser retrying a flaky POST — must not
        // create two listings out of one click.
        var result = await idempotency.RunOnceAsync(
            "listing.create",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(command, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(new { listingId = result.ListingId, result.Published, result.CanonicalPath });
    }

    /// <summary>
    /// (Re-)publishes a draft or archived listing — the "list" side of list/delist.
    /// </summary>
    /// <remarks>
    /// <see cref="Listing.Publish"/> already accepts either starting state, and already
    /// throws <c>listing.photo_required</c> if there is nothing to show a buyer — this
    /// endpoint adds only who may call it and re-indexing, same shape as
    /// <c>CreateListingHandler</c>'s own publish step.
    /// </remarks>
    private static async Task<IResult> PublishListingAsync(
        string id,
        ICurrentUser user,
        IListingRepository listings,
        IAuctionRepository auctions,
        IListingSearchIndex searchIndex,
        IIdempotencyStore idempotency,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        var listing = await listings.FindAsync(id, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        // Same reasoning as ArchiveListingAsync: checked before the idempotency wrapper,
        // so a replay cannot short-circuit the ownership check.
        listing.EnsureOwnedBy(user.RequireUserId());

        var sellerUsername = user.Username
            ?? throw DomainException.For("listing.no_username", "Your account is missing a username.");

        var response = await idempotency.RunOnceAsync(
            "listing.publish",
            Read(http),
            TimeSpan.FromMinutes(2),
            async () =>
            {
                var expectedVersion = listing.Version;
                listing.Publish(clock);
                await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

                DateTimeOffset? auctionEndsAt = null;
                var bidCount = 0;
                if (listing.Format == ListingFormat.Auction)
                {
                    var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
                    auctionEndsAt = auction?.EndsAt;
                    bidCount = auction?.BidCount ?? 0;
                }

                // The mirror image of ArchiveListingAsync's RemoveAsync: relisting must
                // put the listing back in browse, not just flip its own status column.
                await searchIndex.IndexAsync(
                    new ListingIndexEntry(listing, sellerUsername, auctionEndsAt, bidCount), ct)
                    .ConfigureAwait(false);

                return new { listing.Id, Status = listing.Status.ToString() };
            },
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> ArchiveListingAsync(
        string id,
        ICurrentUser user,
        IListingRepository listings,
        IListingSearchIndex searchIndex,
        IIdempotencyStore idempotency,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        var listing = await listings.FindAsync(id, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        // Thrown before the idempotency wrapper: an ownership check is not something a
        // replay should be able to short-circuit, and it needs nothing recorded to be
        // consistent across repeats — the same caller gets the same refusal every time.
        listing.EnsureOwnedBy(user.RequireUserId());

        var response = await idempotency.RunOnceAsync(
            "listing.archive",
            Read(http),
            TimeSpan.FromMinutes(2),
            async () =>
            {
                var expectedVersion = listing.Version;
                listing.Archive(clock);
                await listings.UpdateAsync(listing, expectedVersion, ct).ConfigureAwait(false);

                // Withdrawing a listing must also withdraw it from browse — an archived
                // item still sitting in the search index is a dead link away from a page
                // that now refuses to render it.
                await searchIndex.RemoveAsync(listing.Id, ct).ConfigureAwait(false);

                return new { listing.Id, Status = listing.Status.ToString() };
            },
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetMyListingsAsync(
        ICurrentUser user,
        IListingRepository listings,
        ICategoryRepository categories,
        IAuctionRepository auctions,
        CancellationToken ct)
    {
        var page = await listings
            .ListBySellerAsync(user.RequireUserId(), status: null, cursor: null, limit: 50, ct)
            .ConfigureAwait(false);

        // A seller's own listing count is small enough that a lookup per row costs
        // nothing worth batching for — the browse index's batched approach exists to
        // serve pages of strangers' listings at once, not this.
        var categoryCache = new Dictionary<string, Category?>(StringComparer.Ordinal);
        var items = new List<object>(page.Items.Count);

        foreach (var listing in page.Items)
        {
            if (!categoryCache.TryGetValue(listing.CategoryId, out var category))
            {
                category = await categories.FindByIdAsync(listing.CategoryId, ct).ConfigureAwait(false);
                categoryCache[listing.CategoryId] = category;
            }

            DateTimeOffset? auctionEndsAt = null;
            var bidCount = 0;
            if (listing.Format == ListingFormat.Auction)
            {
                var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
                auctionEndsAt = auction?.EndsAt;
                bidCount = auction?.BidCount ?? 0;
            }

            items.Add(new
            {
                listing.Id,
                listing.Title,
                listing.CategoryId,
                CategorySlug = category?.Slug,
                PriceRupiah = listing.Price.Rupiah,
                Format = listing.Format.ToString(),
                Status = listing.Status.ToString(),
                listing.Condition,
                SellerUsername = user.Username,
                listing.City,
                PrimaryPhotoKey = listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                AuctionEndsAt = auctionEndsAt,
                BidCount = bidCount,
                listing.ViewCount,
                listing.WatcherCount,
            });
        }

        return Results.Ok(new { items });
    }

    /// <param name="Format"><c>FixedPrice</c>, <c>AcceptOffers</c>, or <c>Auction</c>.</param>
    /// <param name="Photos">Empty for a draft; publishing requires at least one.</param>
    /// <param name="AutoDeclineBelowPercent">Only meaningful when <see cref="Format"/> is <c>AcceptOffers</c>.</param>
    /// <param name="AuctionDurationDays">Only meaningful when <see cref="Format"/> is <c>Auction</c>.</param>
    /// <param name="ReservePriceRupiah">
    /// Only meaningful when <see cref="Format"/> is <c>Auction</c>. Shown openly, not hidden.
    /// </param>
    /// <param name="BuyoutPriceRupiah">
    /// Only meaningful when <see cref="Format"/> is <c>Auction</c>. Ends the auction
    /// immediately as a sale whenever a buyer pays it, even mid-bidding.
    /// </param>
    /// <param name="Publish"><c>false</c> saves a draft; <c>true</c> publishes immediately.</param>
    private sealed record CreateListingRequest(
        string CategoryId,
        string Title,
        long PriceRupiah,
        string Format,
        string Condition,
        string City,
        string? Description,
        IReadOnlyList<PhotoRequest>? Photos,
        decimal? AutoDeclineBelowPercent,
        int? AuctionDurationDays,
        long? ReservePriceRupiah,
        long? BuyoutPriceRupiah,
        bool Publish);

    private sealed record PhotoRequest(string StorageKey, int Width, int Height);
}
