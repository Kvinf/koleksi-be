using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Reviews;
using Koleksi.Domain.Reviews;
using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>A buyer's review of an order.</summary>
/// <remarks>
/// <para>
/// Both routes need an identity and answer <c>404</c> for an order that is missing or not the
/// caller's own purchase, so an order id cannot be probed. The eligibility rules (buyer only, once
/// per order, only after delivery) are enforced in the handler and the domain, not here.
/// </para>
/// <para>
/// The public side of reviews, the seller's feed, is under <c>/api/sellers/{handle}/reviews</c>.
/// </para>
/// </remarks>
internal static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/orders/{orderId}/review", SubmitAsync)
            .RequireRateLimiting(RateLimitPolicies.Reviews)
            .WithTags("Reviews")
            .WithName("SubmitOrderReview");

        app.MapGet("/api/orders/{orderId}/review", GetAsync)
            .WithTags("Reviews")
            .WithName("GetOrderReview");

        return app;
    }

    private static async Task<IResult> SubmitAsync(
        string orderId,
        SubmitReviewRequest request,
        SubmitReviewHandler handler,
        IIdempotencyStore idempotency,
        ICurrentUser user,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Rating is < Review.MinRating or > Review.MaxRating)
        {
            return Results.BadRequest(new { code = "review.invalid_rating" });
        }

        // Bounded before the handler sees it: the domain trims first, so this only stops a
        // payload far beyond anything a person typed from reaching the string-cleaning code.
        if (request.Text is { Length: > Review.MaxTextLength * 2 })
        {
            return Results.BadRequest(new { code = "review.text_too_long" });
        }

        // A double-tapped "Kirim" must not turn into "already reviewed" for a review that did save.
        var result = await idempotency.RunOnceAsync(
            $"review.submit:{user.RequireUserId()}:{orderId}",
            Read(http),
            TimeSpan.FromMinutes(5),
            () => handler.HandleAsync(
                new SubmitReviewCommand(orderId, request.Rating, request.Text, request.Anonymous), ct),
            ct).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { reviewId = result.ReviewId })
            : Results.NotFound(new { code = "review.order_not_found" });
    }

    private static async Task<IResult> GetAsync(string orderId, GetOrderReviewHandler handler, CancellationToken ct)
    {
        var state = await handler.HandleAsync(orderId, ct).ConfigureAwait(false);
        if (state is null)
        {
            return Results.NotFound(new { code = "review.order_not_found" });
        }

        return Results.Ok(new
        {
            eligible = state.Eligible,
            review = state.Review is { } review
                ? new
                {
                    review.Id,
                    review.Rating,
                    review.Text,
                    anonymous = review.IsAnonymous,
                    review.CreatedAt,
                }
                : null,
        });
    }

    private sealed record SubmitReviewRequest(int Rating, string? Text, bool Anonymous);
}
