using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Auctions;
using Microsoft.AspNetCore.RateLimiting;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Bidding.
/// </summary>
/// <remarks>
/// <para>
/// One write, and it is the most sensitive one in the product. It moves money-equivalent
/// state, it is the most abusable endpoint we expose, and it is the only place where two
/// requests genuinely race.
/// </para>
/// <para>
/// Three controls apply, each doing a different job: the group inherits the application's
/// fallback authentication policy, so an anonymous bid is impossible; the bidder's
/// identity is read from the validated token inside the handler, never from the body, so
/// bidding as someone else is impossible; and a tighter rate-limit bucket keyed on the
/// caller's subject bounds how fast one account can hammer a lot.
/// </para>
/// </remarks>
internal static class AuctionEndpoints
{
    public static IEndpointRouteBuilder MapAuctionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auctions")
            .WithTags("Auctions");

        group.MapPost("/{listingId}/bids", PlaceBidAsync)
            .RequireRateLimiting(RateLimitPolicies.Bidding)
            .WithName("PlaceBid");

        group.MapPost("/{listingId}/buyout", BuyoutAsync)
            .RequireRateLimiting(RateLimitPolicies.Bidding)
            .WithName("Buyout");

        // Live price updates are not served from here. Every accepted bid or buyout is pushed on
        // the listing's public "auction:" topic over the WebSocket connection — see
        // RealtimeAuctionEventPublisher.

        return app;
    }

    private static async Task<IResult> PlaceBidAsync(
        string listingId,
        PlaceBidRequest request,
        PlaceBidHandler handler,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The listing comes from the route and the bidder from the token. The body carries
        // one number and nothing else, so there is no field in it worth forging.
        var result = await handler
            .HandleAsync(new PlaceBidCommand(listingId, request.AmountRupiah), ct)
            .ConfigureAwait(false);

        if (!result.Found)
        {
            return Results.NotFound(new { code = "auction.not_found" });
        }

        // Note what is absent: OutbidUserId, which is for notification, not for the
        // bidder who just placed this bid.
        return Results.Ok(new
        {
            result.IsLeading,
            result.CurrentPriceRupiah,
            result.NextMinimumBidRupiah,
            result.BidCount,
            result.EndsAt,
            result.WasExtended,
        });
    }

    /// <param name="AmountRupiah">The bid amount — becomes the new visible price outright.</param>
    /// <remarks>
    /// Carries no validation attribute on purpose. Minimal APIs do not run data
    /// annotations unless validation is explicitly added to the service collection, so an
    /// attribute here would read as an input control while enforcing nothing — which is
    /// worse than no attribute at all. The amount is checked where it has to be anyway:
    /// <c>Money.TryFromRupiah</c> refuses a negative, and the auction's own rules refuse
    /// anything below the next minimum bid.
    /// </remarks>
    private sealed record PlaceBidRequest(long AmountRupiah);

    private static async Task<IResult> BuyoutAsync(
        string listingId,
        BuyoutHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        // A double-tap on "Buy now" replays the first answer instead of failing the second tap
        // with a confusing "auction is closed". No key, no protection.
        var result = await idempotency.RunOnceAsync(
            $"auction.buyout:{user.RequireUserId()}:{listingId}",
            IdempotencyHeader.Read(http),
            TimeSpan.FromMinutes(10),
            () => handler.HandleAsync(listingId, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { result.FinalPriceRupiah })
            : Results.NotFound(new { code = "auction.not_found" });
    }
}
