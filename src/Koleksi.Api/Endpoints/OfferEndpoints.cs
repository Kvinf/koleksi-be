using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Offers;
using Koleksi.Domain.Offers;
using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>Making, answering, and listing offers on <c>nego</c> listings.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c> anywhere in this group — every route needs an identity. Which
/// side of an offer the caller is on, and whether that side may act in the offer's current
/// state, is decided inside the domain from <see cref="ICurrentUser"/>, never from the route
/// or body, the same reasoning every other write in this product follows.
/// </remarks>
internal static class OfferEndpoints
{
    /// <summary>How long a replayed answer is remembered: long enough for a double tap or a retry.</summary>
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(2);

    public static IEndpointRouteBuilder MapOfferEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/listings/{listingId}/offers", MakeOfferAsync).WithTags("Offers").WithName("MakeOffer");

        var group = app.MapGroup("/api/offers").WithTags("Offers");

        // Accept and decline answer for whichever side it is the turn of: the seller on a
        // pending offer, the buyer on a counter. The domain refuses anyone else.
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

    private static async Task<IResult> AcceptOfferAsync(
        string offerId, AcceptOfferHandler handler, ICurrentUser user, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct) =>
        ToResult(await idempotency.RunOnceAsync(
            $"offer.accept:{user.RequireUserId()}:{offerId}", Read(http), IdempotencyRetention, () => handler.HandleAsync(offerId, ct), ct,
            InFlightPolicy.Reject)
            .ConfigureAwait(false));

    private static async Task<IResult> DeclineOfferAsync(
        string offerId, DeclineOfferHandler handler, ICurrentUser user, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct) =>
        ToResult(await idempotency.RunOnceAsync(
            $"offer.decline:{user.RequireUserId()}:{offerId}", Read(http), IdempotencyRetention, () => handler.HandleAsync(offerId, ct), ct)
            .ConfigureAwait(false));

    private static async Task<IResult> CounterOfferAsync(
        string offerId,
        CounterOfferRequest request,
        CounterOfferHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ToResult(await idempotency.RunOnceAsync(
            $"offer.counter:{user.RequireUserId()}:{offerId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(offerId, request.CounterAmountRupiah, ct),
            ct,
            InFlightPolicy.Reject).ConfigureAwait(false));
    }

    private static async Task<IResult> WithdrawOfferAsync(
        string offerId, WithdrawOfferHandler handler, ICurrentUser user, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct) =>
        ToResult(await idempotency.RunOnceAsync(
            $"offer.withdraw:{user.RequireUserId()}:{offerId}", Read(http), IdempotencyRetention, () => handler.HandleAsync(offerId, ct), ct)
            .ConfigureAwait(false));

    private static async Task<IResult> GetOfferAsync(string offerId, OfferViews views, CancellationToken ct)
    {
        // Same coarse answer for "does not exist" and "not yours" — same reasoning
        // OrderEndpoints.GetOrderAsync uses.
        var view = await views.GetAsync(offerId, ct).ConfigureAwait(false);
        return view is null ? Results.NotFound(new { code = "offer.not_found" }) : Results.Ok(ToOfferRow(view));
    }

    private static IResult ToResult(OfferOutcome outcome) => outcome.Found
        ? Results.Ok(new
        {
            status = outcome.Status.ToString(),
            amountRupiah = outcome.AmountRupiah,
            counterAmountRupiah = outcome.CounterAmountRupiah,
            outcome.ExpiresAt,
        })
        : Results.NotFound(new { code = "offer.not_found" });

    private static async Task<IResult> ListMyOffersAsync(OfferViews views, string? cursor, CancellationToken ct)
    {
        var page = await views.ListSentAsync(cursor, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOfferRow), nextCursor = page.NextCursor });
    }

    private static async Task<IResult> ListReceivedOffersAsync(OfferViews views, string? cursor, CancellationToken ct)
    {
        var page = await views.ListReceivedAsync(cursor, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOfferRow), nextCursor = page.NextCursor });
    }

    /// <summary>
    /// One offer as its viewer sees it. No user id appears: each party is named by handle,
    /// and the viewer's own side is stated as <c>viewerRole</c>.
    /// </summary>
    private static object ToOfferRow(OfferView view) => new
    {
        view.Offer.Id,
        view.Offer.ListingId,
        listingTitle = view.ListingTitle,
        listingPhotoKey = view.ListingPhotoKey,
        categorySlug = view.CategorySlug,
        viewerRole = view.ViewerRole == OfferParty.Buyer ? "buyer" : "seller",
        sellerUsername = view.SellerHandle,
        buyerUsername = view.BuyerHandle,
        amountRupiah = view.Offer.Amount.Rupiah,
        counterAmountRupiah = view.Offer.CounterAmount?.Rupiah,
        currentAmountRupiah = view.Offer.CurrentAmount.Rupiah,
        askingPriceRupiah = view.Offer.AskingPrice.Rupiah,
        percentOfAsking = view.Offer.PercentOfAsking,
        view.Offer.Message,
        status = view.Status.ToString(),
        view.Offer.CreatedAt,
        view.Offer.ExpiresAt,
        view.OrderId,
        view.CanCheckout,
    };

    private sealed record MakeOfferRequest(long AmountRupiah, string? Message);

    private sealed record CounterOfferRequest(long CounterAmountRupiah);
}
