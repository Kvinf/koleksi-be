using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Endpoints;

/// <summary>Checkout, and a buyer's or seller's own orders.</summary>
/// <remarks>
/// No <c>AllowAnonymous</c> anywhere here — every route needs an identity, either as the
/// buyer checking out or reading their own purchases, or as the seller fulfilling one.
/// The payment gateway's own callback is a separate group entirely: see
/// <c>PaymentEndpoints</c>, which is anonymous by necessity and authenticates by HMAC
/// signature instead of a bearer token.
/// </remarks>
internal static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/checkout", CheckoutAsync).WithName("Checkout");
        group.MapGet("/mine", ListMyOrdersAsync).WithName("ListMyOrders");
        group.MapGet("/selling", ListMySalesAsync).WithName("ListMySales");
        group.MapGet("/{orderId}", GetOrderAsync).WithName("GetOrder");
        group.MapPost("/{orderId}/shipment", MarkShippedAsync).WithName("MarkOrderShipped");
        group.MapPost("/{orderId}/release", ConfirmDeliveryAsync).WithName("ConfirmOrderDelivery");
        group.MapPost("/{orderId}/dispute", OpenDisputeAsync).WithName("OpenOrderDispute");

        return app;
    }

    private static async Task<IResult> CheckoutAsync(
        CheckoutRequest request, CheckoutHandler handler, IIdentityService identity, HttpRequest http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<CheckoutSource>(request.Source, ignoreCase: false, out var source))
        {
            return Results.BadRequest(new { code = "order.invalid_source" });
        }

        // Resolved from the caller's own bearer token, never trusted from the body — see
        // CheckoutCommand.BuyerEmail's own remarks.
        var bearerToken = BearerToken.Read(http);
        var email = bearerToken is null ? null : await identity.GetEmailAsync(bearerToken, ct).ConfigureAwait(false);
        if (email is null)
        {
            // Cannot happen for a real, authenticated caller — the fallback policy already
            // required a valid token — but a coarse refusal is cheaper than trusting it.
            return Results.Json(new { code = "order.forbidden" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var command = new CheckoutCommand(
            source,
            request.ReferenceId,
            email,
            new ShippingAddressInput(
                request.ShipTo.RecipientName, request.ShipTo.Phone, request.ShipTo.Street,
                request.ShipTo.City, request.ShipTo.Province, request.ShipTo.PostalCode),
            request.CourierCode);

        var result = await handler.HandleAsync(command, ct).ConfigureAwait(false);

        if (!result.Found)
        {
            return Results.NotFound(new { code = "order.reference_not_found" });
        }

        return Results.Ok(new
        {
            orderId = result.OrderId,
            orderNumber = result.OrderNumber,
            grandTotalRupiah = result.GrandTotalRupiah,
            paymentRedirectUrl = result.PaymentRedirectUrl?.ToString(),
            paymentQrisPayload = result.PaymentQrisPayload,
            paymentUnavailable = result.PaymentUnavailable,
        });
    }

    private static async Task<IResult> GetOrderAsync(
        string orderId, ICurrentUser user, IOrderRepository orders, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        // Same coarse answer for "does not exist" and "not yours" — distinguishing them
        // would let this endpoint be used to probe which order ids are real.
        var callerId = user.RequireUserId();
        var isBuyer = string.Equals(order.BuyerId, callerId, StringComparison.Ordinal);
        var isSeller = string.Equals(order.SellerId, callerId, StringComparison.Ordinal);
        if (!isBuyer && !isSeller)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        // The frontend only ever knows the caller's own username, never their raw Cognito
        // subject — SessionUser carries no id — so it cannot compare order.buyerId/sellerId
        // itself the way GetListingHandler.IsOwnListing does server-side. Naming the role
        // here is that same derivation, just returned instead of applied.
        return Results.Ok(new { order = ToOrderDetail(order), viewerRole = isBuyer ? "buyer" : "seller" });
    }

    private static async Task<IResult> ListMyOrdersAsync(
        ICurrentUser user, IOrderRepository orders, string? cursor, CancellationToken ct)
    {
        var page = await orders.ListForBuyerAsync(user.RequireUserId(), cursor, limit: 20, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOrderRow), nextCursor = page.NextCursor });
    }

    private static async Task<IResult> ListMySalesAsync(
        ICurrentUser user, IOrderRepository orders, string? cursor, CancellationToken ct)
    {
        var page = await orders.ListForSellerAsync(user.RequireUserId(), cursor, limit: 20, ct).ConfigureAwait(false);
        return Results.Ok(new { items = page.Items.Select(ToOrderRow), nextCursor = page.NextCursor });
    }

    private static async Task<IResult> MarkShippedAsync(
        string orderId, MarkShippedRequest request, MarkOrderShippedHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var command = new MarkOrderShippedCommand(
            orderId, request.TrackingNumber, request.PackedParcelPhotoKey,
            request.TrackingLabelPhotoKey, request.ShippedOn);

        var outcome = await handler.HandleAsync(command, ct).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    private static async Task<IResult> ConfirmDeliveryAsync(
        string orderId, ConfirmDeliveryHandler handler, CancellationToken ct)
    {
        var outcome = await handler.HandleAsync(orderId, ct).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    private static async Task<IResult> OpenDisputeAsync(
        string orderId, OpenDisputeRequest request, OpenOrderDisputeHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = await handler.HandleAsync(orderId, request.Reason, ct).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    private static object ToOrderRow(Order order) => new
    {
        order.Id,
        order.OrderNumber,
        order.BuyerId,
        order.SellerId,
        title = order.Lines.Count > 0 ? order.Lines[0].Title : null,
        grandTotalRupiah = order.GrandTotal.Rupiah,
        escrowState = order.EscrowState.ToString(),
        order.CreatedAt,
    };

    private static object ToOrderDetail(Order order) => new
    {
        order.Id,
        order.OrderNumber,
        order.BuyerId,
        order.SellerId,
        lines = order.Lines.Select(line => new
        {
            line.ListingId,
            line.Title,
            line.PhotoKey,
            unitPriceRupiah = line.UnitPrice.Rupiah,
            line.Quantity,
        }),
        shipTo = new
        {
            order.ShipTo.RecipientName,
            order.ShipTo.Phone,
            order.ShipTo.Street,
            order.ShipTo.City,
            order.ShipTo.Province,
            order.ShipTo.PostalCode,
        },
        courier = new
        {
            order.Courier.CourierCode,
            order.Courier.ServiceName,
            costRupiah = order.Courier.Cost.Rupiah,
            order.Courier.EstimatedDays,
        },
        itemsTotalRupiah = order.ItemsTotal.Rupiah,
        shippingTotalRupiah = order.ShippingTotal.Rupiah,
        grandTotalRupiah = order.GrandTotal.Rupiah,
        escrowState = order.EscrowState.ToString(),
        order.CreatedAt,
        order.PaidAt,
        order.ShippedAt,
        order.DeliveredAt,
        order.ReleasedAt,
        autoReleaseAt = order.AutoReleaseAt,
        trackingNumber = order.Shipment?.TrackingNumber,
        disputeOpen = order.Dispute is { Resolution: null },
    };

    private sealed record ShippingAddressRequest(
        string RecipientName, string Phone, string Street, string City, string Province, string PostalCode);

    private sealed record CheckoutRequest(
        string Source, string ReferenceId, ShippingAddressRequest ShipTo, string CourierCode);

    private sealed record MarkShippedRequest(
        string TrackingNumber, string PackedParcelPhotoKey, string TrackingLabelPhotoKey, DateOnly ShippedOn);

    private sealed record OpenDisputeRequest(string Reason);
}
