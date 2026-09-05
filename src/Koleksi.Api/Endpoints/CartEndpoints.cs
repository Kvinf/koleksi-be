using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Cart;
using Koleksi.Application.Features.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>The buyer's cart: add, remove, list, and check out everything selected at once.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c> anywhere here — a cart belongs to a signed-in buyer, the same
/// reasoning <see cref="OrderEndpoints"/> gives for its own group.
/// </remarks>
internal static class CartEndpoints
{
    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/cart").WithTags("Cart");

        group.MapGet("", GetCartAsync).WithName("GetCart");
        group.MapPost("/items", AddCartItemAsync).WithName("AddCartItem");
        group.MapDelete("/items/{listingId}", RemoveCartItemAsync).WithName("RemoveCartItem");
        group.MapPost("/checkout", CheckoutCartAsync).WithName("CheckoutCart");

        return app;
    }

    private static async Task<IResult> GetCartAsync(GetCartHandler handler, CancellationToken ct)
    {
        var view = await handler.HandleAsync(ct).ConfigureAwait(false);
        return Results.Ok(new { items = view.Items.Select(ToRow) });
    }

    private static async Task<IResult> AddCartItemAsync(
        AddCartItemRequest request, AddToCartHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ListingId))
        {
            return Results.BadRequest(new { code = "cart.invalid_details" });
        }

        var result = await handler.HandleAsync(request.ListingId, ct).ConfigureAwait(false);

        return result.Found
            ? Results.Ok(new { added = true })
            : Results.NotFound(new { code = "cart.item_not_found" });
    }

    private static async Task<IResult> RemoveCartItemAsync(
        string listingId, RemoveFromCartHandler handler, CancellationToken ct)
    {
        await handler.HandleAsync(listingId, ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> CheckoutCartAsync(
        CheckoutCartRequest request,
        CheckoutCartHandler handler,
        IIdentityService identity,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ListingIds is not { Count: > 0 })
        {
            return Results.BadRequest(new { code = "cart.checkout_empty" });
        }

        // Resolved from the caller's own bearer token, never trusted from the body — same
        // reasoning as OrderEndpoints.CheckoutAsync's own BuyerEmail resolution.
        var bearerToken = BearerToken.Read(http);
        var email = bearerToken is null ? null : await identity.GetEmailAsync(bearerToken, ct).ConfigureAwait(false);
        if (email is null)
        {
            return Results.Json(new { code = "order.forbidden" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var command = new CheckoutCartCommand(
            request.ListingIds,
            email,
            new ShippingAddressInput(
                request.ShipTo.RecipientName, request.ShipTo.Phone, request.ShipTo.Street,
                request.ShipTo.City, request.ShipTo.Province, request.ShipTo.PostalCode),
            request.CourierCodeBySellerId ?? new Dictionary<string, string>(StringComparer.Ordinal));

        var result = await handler.HandleAsync(command, ct).ConfigureAwait(false);

        if (!result.Found)
        {
            return Results.NotFound(new { code = "cart.nothing_purchasable" });
        }

        return Results.Ok(new
        {
            orders = result.Orders.Select(order => new
            {
                sellerId = order.SellerId,
                orderId = order.OrderId,
                orderNumber = order.OrderNumber,
                grandTotalRupiah = order.GrandTotalRupiah,
                paymentRedirectUrl = order.PaymentRedirectUrl?.ToString(),
                paymentQrisPayload = order.PaymentQrisPayload,
                paymentUnavailable = order.PaymentUnavailable,
            }),
        });
    }

    private static object ToRow(CartRowView row) => new
    {
        listingId = row.ListingId,
        title = row.Title,
        categorySlug = row.CategorySlug,
        priceRupiah = row.PriceRupiah,
        condition = row.Condition,
        sellerId = row.SellerId,
        sellerUsername = row.SellerUsername,
        sellerCity = row.SellerCity,
        photoUrl = row.PhotoUrl,
        addedAt = row.AddedAt,
    };

    private sealed record AddCartItemRequest(string ListingId);

    private sealed record ShippingAddressRequest(
        string RecipientName, string Phone, string Street, string City, string Province, string PostalCode);

    private sealed record CheckoutCartRequest(
        IReadOnlyList<string> ListingIds,
        ShippingAddressRequest ShipTo,
        IReadOnlyDictionary<string, string>? CourierCodeBySellerId);
}
