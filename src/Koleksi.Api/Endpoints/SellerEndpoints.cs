using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;
using Microsoft.AspNetCore.Mvc;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// The public storefront of a seller, and the signed-in user's own public identity.
/// </summary>
/// <remarks>
/// <para>
/// Storefront reads are <c>AllowAnonymous</c> for the same reason the catalogue is: a shop page
/// has to be crawlable. They are addressed by the public handle only. Nothing here accepts, stores
/// or returns the sign-in name, an email address, or an account id.
/// </para>
/// <para>
/// An unknown handle and a malformed one both answer <c>404</c>, so the handle space cannot be
/// probed for anything but "is there a shop by this name".
/// </para>
/// </remarks>
internal static class SellerEndpoints
{
    public static IEndpointRouteBuilder MapSellerEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var shops = app.MapGroup("/api/sellers").AllowAnonymous().WithTags("Sellers");
        shops.MapGet("/{handle}", GetStorefrontAsync).WithName("GetSellerStorefront");
        shops.MapGet("/{handle}/listings", GetListingsAsync).WithName("GetSellerListings");
        shops.MapGet("/{handle}/reviews", GetReviewsAsync).WithName("GetSellerReviews");

        // The caller's own handle and display name. Authenticated by the fallback policy.
        var own = app.MapGroup("/api/profile/public").WithTags("Profile");
        own.MapGet("", GetOwnAsync).WithName("GetOwnPublicProfile");
        own.MapPut("", UpdateOwnAsync).WithName("UpdateOwnPublicProfile");

        return app;
    }

    private static async Task<IResult> GetStorefrontAsync(
        string handle, GetSellerStorefrontHandler handler, HttpContext context, CancellationToken ct)
    {
        var view = await handler.HandleAsync(handle, ct).ConfigureAwait(false);
        if (view is null)
        {
            return Results.NotFound(new { code = "seller.not_found" });
        }

        MarkPubliclyCacheable(context);

        return Results.Ok(new
        {
            view.Handle,
            view.DisplayName,
            view.MemberSince,
            view.AvatarUrl,
            view.ContactVerified,
            view.ActiveListingCount,
            view.SoldCount,
            rating = new
            {
                average = view.Rating.Average,
                count = view.Rating.Count,

                // Index 0 is one star, index 4 five stars.
                histogram = view.Rating.Histogram,
            },
        });
    }

    private static async Task<IResult> GetListingsAsync(
        string handle,
        [FromQuery(Name = "kind")] string? kind,
        [FromQuery(Name = "cursor")] string? cursor,
        ListSellerListingsHandler handler,
        HttpContext context,
        CancellationToken ct)
    {
        var parsedKind = string.Equals(kind, "auctions", StringComparison.Ordinal)
            ? StorefrontListingKind.Auctions
            : StorefrontListingKind.Items;

        var page = await handler.HandleAsync(handle, parsedKind, cursor, ct).ConfigureAwait(false);
        if (page is null)
        {
            return Results.NotFound(new { code = "seller.not_found" });
        }

        MarkPubliclyCacheable(context);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> GetReviewsAsync(
        string handle,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] int? limit,
        ListSellerReviewsHandler handler,
        HttpContext context,
        CancellationToken ct)
    {
        var page = await handler.HandleAsync(handle, cursor, limit, ct).ConfigureAwait(false);
        if (page is null)
        {
            return Results.NotFound(new { code = "seller.not_found" });
        }

        MarkPubliclyCacheable(context);
        return Results.Ok(new { items = page.Items, nextCursor = page.NextCursor });
    }

    private static async Task<IResult> GetOwnAsync(GetOwnPublicProfileHandler handler, CancellationToken ct) =>
        Results.Ok(await handler.HandleAsync(ct).ConfigureAwait(false));

    private static async Task<IResult> UpdateOwnAsync(
        UpdateOwnPublicProfileRequest request,
        UpdateOwnPublicProfileHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Validated at the boundary so the caller gets a 400 with the rule that failed; the
        // domain applies the same rules again when it edits the profile.
        var handleError = PublicHandle.TryNormalize(request.Handle, out _);
        if (handleError is not null)
        {
            return Results.BadRequest(new { code = handleError });
        }

        var nameError = PublicDisplayName.TryNormalize(request.DisplayName, out _);
        if (nameError is not null)
        {
            return Results.BadRequest(new { code = nameError });
        }

        var view = await idempotency.RunOnceAsync(
            $"profile.public:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => handler.HandleAsync(request.Handle, request.DisplayName, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(view);
    }

    /// <summary>
    /// Everything on a storefront is the same for every caller, so a shared cache may hold it
    /// briefly — the same minute the browse grid uses.
    /// </summary>
    private static void MarkPubliclyCacheable(HttpContext context) =>
        context.Response.Headers.CacheControl = "public, max-age=60";

    private sealed record UpdateOwnPublicProfileRequest(string? Handle, string? DisplayName);
}
