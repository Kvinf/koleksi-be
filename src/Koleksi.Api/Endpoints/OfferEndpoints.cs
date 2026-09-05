using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Offers;
using Koleksi.Domain.Offers;

namespace Koleksi.Api.Endpoints;

/// <summary>Making, answering, and listing offers on <c>nego</c> listings.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c> anywhere in this group — every route needs an identity, either
/// as the buyer making or withdrawing an offer, or as the seller answering one. The
/// buyer/seller comes from <see cref="ICurrentUser"/> inside each handler, never from the
/// route or body, the same reasoning every other write in this product follows.
/// </remarks>
internal static class OfferEndpoints
{
    public static IEndpointRouteBuilder MapOfferEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/listings/{listingId}/offers", MakeOfferAsync).WithTags("Offers").WithName("MakeOffer");

        var group = app.MapGroup("/api/offers").WithTags("Offers");

        group.MapPost("/{offerId}/accept", AcceptOfferAsync).WithName("AcceptOffer");
        group.MapPost("/{offerId}/decline", DeclineOfferAsync).WithName("DeclineOffer");
        group.MapPost("/{offerId}/counter", CounterOfferAsync).WithName("CounterOffer");
        group.MapPost("/{offerId}/withdraw", WithdrawOfferAsync).WithName("WithdrawOffer");
        group.MapGet("/{offerId}", GetOfferAsync).WithName("GetOffer");
        group.MapGet("/mine", ListMyOffersAsync).WithName("ListMyOffers");
        group.MapGet("/received", ListReceivedOffersAsync).WithName("ListReceivedOffers");

        return app;
    }

    private static async Task<IResult> MakeOfferAsync(
        string listingId, MakeOfferRequest request, MakeOfferHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await handler
            .HandleAsync(new MakeOfferCommand(listingId, request.AmountRupiah, request.Message), ct)
            .ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { offerId = result.OfferId, status = result.Status.ToString(), result.ExpiresAt })
            : Results.NotFound(new { code = "offer.listing_not_found" });
    }

    private static async Task<IResult> AcceptOfferAsync(string offerId, AcceptOfferHandler handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(offerId, ct).ConfigureAwait(false));

    private static async Task<IResult> DeclineOfferAsync(string offerId, DeclineOfferHandler handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(offerId, ct).ConfigureAwait(false));

    private static async Task<IResult> CounterOfferAsync(
        string offerId, CounterOfferRequest request, CounterOfferHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ToResult(await handler.HandleAsync(offerId, request.CounterAmountRupiah, ct).ConfigureAwait(false));
    }

    private static async Task<IResult> WithdrawOfferAsync(string offerId, WithdrawOfferHandler handler, CancellationToken ct) =>
        ToResult(await handler.HandleAsync(offerId, ct).ConfigureAwait(false));

    private static async Task<IResult> GetOfferAsync(
        string offerId, ICurrentUser user, IOfferRepository offers, CancellationToken ct)
    {
        var offer = await offers.FindAsync(offerId, ct).ConfigureAwait(false);
        if (offer is null)
        {
            return Results.NotFound(new { code = "offer.not_found" });
        }

        // Same coarse answer for "does not exist" and "not yours" — same reasoning
        // OrderEndpoints.GetOrderAsync uses.
        var callerId = user.RequireUserId();
        if (!string.Equals(offer.BuyerId, callerId, StringComparison.Ordinal)
            && !string.Equals(offer.SellerId, callerId, StringComparison.Ordinal))
        {
            return Results.NotFound(new { code = "offer.not_found" });
        }

        return Results.Ok(ToOfferRow(offer));
    }

    private static IResult ToResult(OfferOutcome outcome) => outcome.Found
        ? Results.Ok(new { status = outcome.Status.ToString(), amountRupiah = outcome.AmountRupiah, outcome.ExpiresAt })
        : Results.NotFound(new { code = "offer.not_found" });

    private static async Task<IResult> ListMyOffersAsync(
        ICurrentUser user, IOfferRepository offers, string? cursor, CancellationToken ct)
    {
        var page = await offers.ListForBuyerAsync(user.RequireUserId(), cursor, limit: 20, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOfferRow), nextCursor = page.NextCursor });
    }

    private static async Task<IResult> ListReceivedOffersAsync(
        ICurrentUser user, IOfferRepository offers, string? cursor, CancellationToken ct)
    {
        var page = await offers.ListForSellerAsync(user.RequireUserId(), cursor, limit: 20, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOfferRow), nextCursor = page.NextCursor });
    }

    private static object ToOfferRow(Offer offer) => new
    {
        offer.Id,
        offer.ListingId,
        offer.SellerId,
        offer.BuyerId,
        amountRupiah = offer.Amount.Rupiah,
        askingPriceRupiah = offer.AskingPrice.Rupiah,
        percentOfAsking = offer.PercentOfAsking,
        offer.Message,
        status = offer.Status.ToString(),
        offer.CreatedAt,
        offer.ExpiresAt,
    };

    private sealed record MakeOfferRequest(long AmountRupiah, string? Message);

    private sealed record CounterOfferRequest(long CounterAmountRupiah);
}
