using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Watchlist;
using Microsoft.AspNetCore.RateLimiting;

namespace Koleksi.Api.Endpoints;

/// <summary>The listings a signed-in person has saved ("Simpan").</summary>
/// <remarks>
/// <para>
/// No <c>AllowAnonymous</c> anywhere here: a watchlist belongs to a signed-in account, and
/// whose it is comes from the access token, never from the route or body — so there is no
/// parameter that could name someone else's list.
/// </para>
/// <para>
/// Responses carry listing ids and the same card fields the browse grid serves, and nothing
/// that identifies a person: not the caller's id, and not any other watcher's.
/// </para>
/// </remarks>
internal static class WatchlistEndpoints
{
    public static IEndpointRouteBuilder MapWatchlistEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/watchlist").WithTags("Watchlist");

        group.MapGet("", GetWatchlistAsync).WithName("GetWatchlist");

        // Registered before the "{listingId}" routes so the literal segment wins on its own
        // merits rather than by routing precedence rules a reader has to remember.
        group.MapGet("/ids", GetWatchedIdsAsync).WithName("GetWatchedIds");

        group.MapPut("/{listingId}", SaveAsync)
            .RequireRateLimiting(RateLimitPolicies.Watchlist)
            .WithName("SaveListing");
        group.MapDelete("/{listingId}", UnsaveAsync)
            .RequireRateLimiting(RateLimitPolicies.Watchlist)
            .WithName("UnsaveListing");

        return app;
    }

    private static async Task<IResult> GetWatchlistAsync(
        GetWatchlistHandler handler, IMediaStorage media, int? page, int? size, CancellationToken ct)
    {
        var result = await handler
            .HandleAsync(page ?? 1, size ?? GetWatchlistHandler.DefaultPageSize, ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            items = result.Items.Select(view => new
            {
                view.Listing.Id,
                view.Listing.Title,
                view.Listing.CategoryId,
                view.Listing.CategorySlug,
                view.Listing.PriceRupiah,
                view.Listing.Format,
                view.Listing.Status,
                view.Listing.Condition,
                view.Listing.SellerUsername,
                view.Listing.City,
                view.Listing.PrimaryPhotoKey,
                PrimaryPhotoUrl = media.OriginalUrl(view.Listing.PrimaryPhotoKey),
                view.Listing.AuctionEndsAt,
                view.Listing.BidCount,
                view.SavedAt,
            }),
            page = new
            {
                number = result.PageNumber,
                size = result.PageSize,
                total = result.Total,
                hasNext = result.HasNext,
            },
        });
    }

    private static async Task<IResult> GetWatchedIdsAsync(GetWatchedIdsHandler handler, CancellationToken ct)
    {
        var ids = await handler.HandleAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { ids });
    }

    private static async Task<IResult> SaveAsync(
        string listingId, SaveListingHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(listingId, ct).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { saved = true, changed = result.Changed })
            : Results.NotFound(new { code = "watchlist.listing_not_found" });
    }

    private static async Task<IResult> UnsaveAsync(
        string listingId, UnsaveListingHandler handler, CancellationToken ct)
    {
        var changed = await handler.HandleAsync(listingId, ct).ConfigureAwait(false);

        // Always 200 with the same shape: removing what is not saved is a success, not a
        // 404, so a retried or double-tapped request never shows the person an error.
        return Results.Ok(new { saved = false, changed });
    }
}
