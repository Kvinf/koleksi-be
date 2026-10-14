using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Listings;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Auctions;
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
        group.MapPut("/{id}", UpdateListingAsync).WithName("UpdateListing");
        group.MapPost("/{id}/publish", PublishListingAsync).WithName("PublishListing");
        group.MapPost("/{id}/archive", ArchiveListingAsync).WithName("ArchiveListing");
        group.MapGet("/mine", GetMyListingsAsync).WithName("GetMyListings");
        group.MapGet("/{id}", GetListingForEditAsync).WithName("GetListingForEdit");

        return app;
    }

    private static async Task<IResult> CreateListingAsync(
        CreateListingRequest request,
        CreateListingHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A draft needs only a category and a title; condition, city, price and photos are
        // publish rules, enforced by Listing.Publish.
        if (string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.Title))
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
            request.Condition ?? string.Empty,
            request.City ?? string.Empty,
            request.Description ?? string.Empty,
            (request.Photos ?? []).Select(p => new PhotoInput(p.StorageKey, p.Width, p.Height)).ToList(),
            request.AutoDeclineBelowPercent,
            request.AuctionDurationDays,
            request.ReservePriceRupiah,
            request.BuyoutPriceRupiah,
            request.Publish,
            request.Stock);

        // A double-tap on "Publish" — or a browser retrying a flaky POST — must not
        // create two listings out of one click.
        var result = await idempotency.RunOnceAsync(
            $"listing.create:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(command, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(new { listingId = result.ListingId, result.Published, result.CanonicalPath });
    }

    private static async Task<IResult> UpdateListingAsync(
        string id,
        UpdateListingRequest request,
        UpdateListingHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Same minimum as creating: the rest is a publish rule for a draft, and the domain's
        // own edit rules for a live listing (Listing.EditLive).
        if (string.IsNullOrWhiteSpace(request.CategoryId) || string.IsNullOrWhiteSpace(request.Title))
        {
            return Results.BadRequest(new { code = "listing.invalid_details" });
        }

        if (!Enum.TryParse<ListingFormat>(request.Format, ignoreCase: false, out var format))
        {
            return Results.BadRequest(new { code = "listing.invalid_format" });
        }

        var command = new UpdateListingCommand(
            id,
            request.CategoryId,
            request.Title,
            request.PriceRupiah,
            format,
            request.Condition ?? string.Empty,
            request.City ?? string.Empty,
            request.Description ?? string.Empty,
            (request.Photos ?? []).Select(p => new PhotoInput(p.StorageKey, p.Width, p.Height)).ToList(),
            request.AutoDeclineBelowPercent,
            request.AuctionDurationDays,
            request.ReservePriceRupiah,
            request.BuyoutPriceRupiah,
            request.Publish,
            request.Stock);

        // Same reasoning as CreateListingAsync: a double-tap on "Publish", or a browser
        // retrying a flaky PUT, must not double-apply this edit.
        var result = await idempotency.RunOnceAsync(
            $"listing.update:{user.RequireUserId()}:{id}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(command, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(new { listingId = result.ListingId, result.Published, result.CanonicalPath });
    }

    /// <summary>
    /// (Re-)publishes a draft or archived listing — the "list" side of list/delist. The rules and the
    /// order of writes live in <see cref="PublishListingHandler"/>; this adds who may call it and
    /// replay safety.
    /// </summary>
    private static async Task<IResult> PublishListingAsync(
        string id,
        ICurrentUser user,
        IListingRepository listings,
        PublishListingHandler handler,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        var listing = await listings.FindAsync(id, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        // Checked before the idempotency wrapper, so a replay cannot short-circuit the ownership check.
        listing.EnsureOwnedBy(user.RequireUserId());

        var response = await idempotency.RunOnceAsync(
            $"listing.publish:{user.RequireUserId()}:{id}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(listing, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    /// <summary>
    /// Delists a listing — refused while money is in flight on it (D-01). See <see cref="ArchiveListingHandler"/>.
    /// </summary>
    private static async Task<IResult> ArchiveListingAsync(
        string id,
        ICurrentUser user,
        IListingRepository listings,
        ArchiveListingHandler handler,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        var listing = await listings.FindAsync(id, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        // Thrown before the idempotency wrapper: an ownership check is not something a replay should be
        // able to short-circuit, and the same caller gets the same refusal every time.
        listing.EnsureOwnedBy(user.RequireUserId());

        var response = await idempotency.RunOnceAsync(
            $"listing.archive:{user.RequireUserId()}:{id}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(listing, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> GetMyListingsAsync(
        ICurrentUser user,
        IListingRepository listings,
        ICategoryRepository categories,
        IAuctionRepository auctions,
        PublicIdentityResolver identities,
        string? cursor,
        CancellationToken ct)
    {
        // The first page is the same 50 as before; a seller with more follows nextCursor, which the
        // dashboard's "load more" passes back. The cursor is opaque and only ever resumes the
        // caller's own partition (see DynamoCursor), so a forged one cannot reach another seller.
        var page = await listings
            .ListBySellerAsync(user.RequireUserId(), status: null, cursor, limit: 50, ct)
            .ConfigureAwait(false);

        // A seller's own listing count is small enough that a lookup per row costs
        // nothing worth batching for — the browse index's batched approach exists to
        // serve pages of strangers' listings at once, not this.
        var ownHandle = (await identities.ResolveAsync(user.RequireUserId(), ct).ConfigureAwait(false)).Handle;
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
                SellerUsername = ownHandle,
                listing.City,
                PrimaryPhotoKey = listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null,
                AuctionEndsAt = auctionEndsAt,
                BidCount = bidCount,
                listing.Stock,
                listing.ViewCount,
                listing.WatcherCount,
            });
        }

        return Results.Ok(new { items, nextCursor = page.NextCursor });
    }

    /// <summary>
    /// The sell form's own read of one listing, for editing.
    /// </summary>
    /// <remarks>
    /// Owner-scoped, unlike <c>CatalogEndpoints.GetListingAsync</c> — that one serves the
    /// public item page and refuses anything but Active/Sold, exactly the statuses a draft
    /// never has. This is the other half: it serves any status back to its own seller, and
    /// no one else, which is what lets a draft's edit page load at all.
    /// </remarks>
    private static async Task<IResult> GetListingForEditAsync(
        string id,
        ICurrentUser user,
        IListingRepository listings,
        IAuctionRepository auctions,
        CancellationToken ct)
    {
        var listing = await listings.FindAsync(id, ct).ConfigureAwait(false);
        if (listing is null)
        {
            return Results.NotFound(new { code = "listing.not_found" });
        }

        listing.EnsureOwnedBy(user.RequireUserId());

        // Lets the sell form lock an auction's fields before the seller types into them, instead
        // of letting the save fail with auction.has_bids.
        var auctionHasBids = false;
        if (listing.Format == ListingFormat.Auction)
        {
            var auction = await auctions.FindAsync(listing.Id, ct).ConfigureAwait(false);
            auctionHasBids = auction is { BidCount: > 0 };
        }

        return Results.Ok(new
        {
            // False once the listing has been live: the form then edits only what EditLive allows.
            WasPublished = !listing.IsUnpublished,
            AuctionHasBids = auctionHasBids,
            listing.Id,
            listing.CategoryId,
            listing.Title,
            listing.Description,
            PriceRupiah = listing.Price.Rupiah,
            Format = listing.Format.ToString(),
            Status = listing.Status.ToString(),
            listing.Condition,
            listing.City,
            Photos = listing.Photos.Select(photo => new { photo.StorageKey, photo.Width, photo.Height }),
            listing.Stock,
            listing.AutoDeclineBelowPercent,
            listing.AuctionDurationDays,
            ReservePriceRupiah = listing.AuctionReservePrice?.Rupiah,
            BuyoutPriceRupiah = listing.AuctionBuyoutPrice?.Rupiah,
        });
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
        string? Condition,
        string? City,
        string? Description,
        IReadOnlyList<PhotoRequest>? Photos,
        decimal? AutoDeclineBelowPercent,
        int? AuctionDurationDays,
        long? ReservePriceRupiah,
        long? BuyoutPriceRupiah,
        bool Publish,
        int? Stock = null);

    /// <summary>
    /// Same shape as <see cref="CreateListingRequest"/> — editing a draft fills in the same fields.
    /// For a listing that is or was live only the description, photos, condition, city and (not
    /// for an auction) price are applied; category, format and title must match what is stored.
    /// </summary>
    private sealed record UpdateListingRequest(
        string CategoryId,
        string Title,
        long PriceRupiah,
        string Format,
        string? Condition,
        string? City,
        string? Description,
        IReadOnlyList<PhotoRequest>? Photos,
        decimal? AutoDeclineBelowPercent,
        int? AuctionDurationDays,
        long? ReservePriceRupiah,
        long? BuyoutPriceRupiah,
        bool Publish,
        int? Stock = null);

    private sealed record PhotoRequest(string StorageKey, int Width, int Height);
}
