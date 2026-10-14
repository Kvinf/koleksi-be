using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Sellers;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>Checkout, and a buyer's or seller's own orders.</summary>
/// <remarks>
/// <para>
/// No <c>AllowAnonymous</c> anywhere here — every route needs an identity, either as the
/// buyer checking out or reading their own purchases, or as the seller fulfilling one.
/// The payment gateway's own callback is a separate group entirely: see
/// <c>PaymentEndpoints</c>, which is anonymous by necessity and authenticates by HMAC
/// signature instead of a bearer token.
/// </para>
/// <para>
/// Responses never carry an account id. The two parties are named by their public handle
/// (<c>buyerHandle</c>, <c>sellerHandle</c>) and the caller's own side by <c>viewerRole</c>.
/// </para>
/// </remarks>
internal static class OrderEndpoints
{
    /// <summary>
    /// How long a double-submitted fulfilment or checkout request keeps replaying its first
    /// answer. A payment window is fifteen minutes; ten is ample for a retry and short enough
    /// that a deliberate second checkout later is a new request.
    /// </summary>
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromMinutes(10);

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/orders").WithTags("Orders");

        group.MapPost("/checkout", CheckoutAsync)
            .RequireRateLimiting(RateLimitPolicies.Checkout)
            .WithName("Checkout");
        group.MapGet("/mine", ListMyOrdersAsync).WithName("ListMyOrders");
        group.MapGet("/selling", ListMySalesAsync).WithName("ListMySales");
        group.MapGet("/{orderId}", GetOrderAsync).WithName("GetOrder");
        group.MapPost("/{orderId}/shipment", MarkShippedAsync).WithName("MarkOrderShipped");
        group.MapPost("/{orderId}/release", ConfirmDeliveryAsync).WithName("ConfirmOrderDelivery");
        group.MapPost("/{orderId}/dispute", OpenDisputeAsync).WithName("OpenOrderDispute");
        group.MapPost("/{orderId}/cancel", CancelOrderAsync).WithName("CancelOrder");

        return app;
    }

    private static async Task<IResult> CheckoutAsync(
        CheckoutRequest request,
        CheckoutHandler handler,
        IIdentityService identity,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Enum.TryParse<CheckoutSource>(request.Source, ignoreCase: false, out var source))
        {
            return Results.BadRequest(new { code = "order.invalid_source" });
        }

        // A missing or half-empty address is a malformed request, not a server fault, and it is
        // stored on the order as evidence: every field present and bounded before anything runs.
        var shipTo = request.ShipTo?.ToInput();

        if (string.IsNullOrWhiteSpace(request.ReferenceId) || shipTo is null || !shipTo.IsValid())
        {
            return Results.BadRequest(new { code = "checkout.invalid_details" });
        }

        if (string.IsNullOrWhiteSpace(request.CourierCode))
        {
            return Results.BadRequest(new { code = "order.invalid_courier" });
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

        var command = new CheckoutCommand(source, request.ReferenceId, email, shipTo, request.CourierCode, request.Quantity ?? 1);

        // A double-tap on "Pay", or a retry of a request whose answer never arrived, must not
        // create a second order: the same key replays the first result. No key, no protection.
        var result = await idempotency.RunOnceAsync(
            $"order.checkout:{user.RequireUserId()}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(command, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

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
        string orderId,
        ICurrentUser user,
        IOrderRepository orders,
        PublicIdentityResolver identities,
        IDeliveryTracker tracker,
        CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        // Same coarse answer for "does not exist" and "not yours" — distinguishing them
        // would let this endpoint be used to probe which order ids are real.
        var callerId = user.RequireUserId();
        var viewerRole = RoleOf(order, callerId);
        if (viewerRole is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        // The frontend only ever knows the caller's own username, never their raw Cognito
        // subject — SessionUser carries no id — so it cannot compare the parties itself the
        // way GetListingHandler.IsOwnListing does server-side. Naming the role here is that
        // same derivation, just returned instead of applied. It is also on the order itself,
        // so a row and its detail read alike.
        var handles = await HandlesAsync(identities, [order], ct).ConfigureAwait(false);
        return Results.Ok(new { order = ToOrderDetail(order, viewerRole, handles, tracker.IsConfigured), viewerRole });
    }

    private static async Task<IResult> ListMyOrdersAsync(
        ICurrentUser user, IOrderRepository orders, PublicIdentityResolver identities, string? cursor, CancellationToken ct)
    {
        var callerId = user.RequireUserId();
        var page = await orders.ListForBuyerAsync(callerId, cursor, limit: 20, ct).ConfigureAwait(false);
        var handles = await HandlesAsync(identities, page.Items, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(order => ToOrderRow(order, RoleOf(order, callerId) ?? "buyer", handles)),
            nextCursor = page.NextCursor,
        });
    }

    private static async Task<IResult> ListMySalesAsync(
        ICurrentUser user, IOrderRepository orders, PublicIdentityResolver identities, string? cursor, CancellationToken ct)
    {
        var callerId = user.RequireUserId();
        var page = await orders.ListForSellerAsync(callerId, cursor, limit: 20, ct).ConfigureAwait(false);
        var handles = await HandlesAsync(identities, page.Items, ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            items = page.Items.Select(order => ToOrderRow(order, RoleOf(order, callerId) ?? "seller", handles)),
            nextCursor = page.NextCursor,
        });
    }

    private static async Task<IResult> MarkShippedAsync(
        string orderId,
        MarkShippedRequest request,
        MarkOrderShippedHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PackedParcelPhotoKey) || string.IsNullOrWhiteSpace(request.TrackingLabelPhotoKey))
        {
            return Results.BadRequest(new { code = "shipment.proof_required" });
        }

        // The waybill's length rules (too short, too long) are the domain's, raised by
        // Shipment.Create with their own codes; a blank one is simply "too short".
        var command = new MarkOrderShippedCommand(
            orderId, request.TrackingNumber, request.PackedParcelPhotoKey,
            request.TrackingLabelPhotoKey, request.ShippedOn);

        var outcome = await idempotency.RunOnceAsync(
            $"order.ship:{user.RequireUserId()}:{orderId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(command, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    private static async Task<IResult> ConfirmDeliveryAsync(
        string orderId,
        ConfirmDeliveryHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        var outcome = await idempotency.RunOnceAsync(
            $"order.release:{user.RequireUserId()}:{orderId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(orderId, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    private static async Task<IResult> OpenDisputeAsync(
        string orderId,
        OpenDisputeRequest request,
        OpenOrderDisputeHandler handler,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return Results.BadRequest(new { code = "order.dispute_reason_required" });
        }

        if (reason.Length > Dispute.MaxReasonLength)
        {
            return Results.BadRequest(new { code = "order.dispute_reason_too_long" });
        }

        var outcome = await idempotency.RunOnceAsync(
            $"order.dispute:{user.RequireUserId()}:{orderId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(orderId, reason, ct),
            ct,
            inFlight: InFlightPolicy.Reject).ConfigureAwait(false);

        return outcome.Found
            ? Results.Ok(new { escrowState = outcome.EscrowState.ToString() })
            : Results.NotFound(new { code = "order.not_found" });
    }

    /// <summary>
    /// The buyer cancels an order they have not paid for. Answers the updated order exactly as
    /// <c>GET /api/orders/{orderId}</c> would.
    /// </summary>
    private static async Task<IResult> CancelOrderAsync(
        string orderId,
        CancelOrderHandler handler,
        IOrderRepository orders,
        PublicIdentityResolver identities,
        IDeliveryTracker tracker,
        ICurrentUser user,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        var callerId = user.RequireUserId();

        // A retry with the same key replays the first outcome instead of failing on the order
        // already being cancelled. The scope names the order, so one key cannot cross orders.
        var outcome = await idempotency.RunOnceAsync(
            $"order.cancel:{callerId}:{orderId}",
            Read(http),
            IdempotencyRetention,
            () => handler.HandleAsync(orderId, ct),
            ct).ConfigureAwait(false);

        if (!outcome.Found || outcome.OrderId is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        var order = await orders.FindAsync(outcome.OrderId, ct).ConfigureAwait(false);
        var viewerRole = order is null ? null : RoleOf(order, callerId);
        if (order is null || viewerRole is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        var handles = await HandlesAsync(identities, [order], ct).ConfigureAwait(false);
        return Results.Ok(new
        {
            order = ToOrderDetail(order, viewerRole, handles, tracker.IsConfigured),
            viewerRole,
            escrowState = order.EscrowState.ToString(),
        });
    }

    /// <summary>"buyer", "seller", or <c>null</c> for someone who is neither.</summary>
    private static string? RoleOf(Order order, string callerId) =>
        string.Equals(order.BuyerId, callerId, StringComparison.Ordinal) ? "buyer"
        : string.Equals(order.SellerId, callerId, StringComparison.Ordinal) ? "seller"
        : null;

    /// <summary>The public handle of every party to the given orders, keyed by account id.</summary>
    private static Task<IReadOnlyDictionary<string, PublicProfile>> HandlesAsync(
        PublicIdentityResolver identities, IEnumerable<Order> orders, CancellationToken ct) =>
        identities.ResolveManyAsync(orders.SelectMany(order => new[] { order.BuyerId, order.SellerId }), ct);

    private static object ToOrderRow(Order order, string viewerRole, IReadOnlyDictionary<string, PublicProfile> handles) => new
    {
        order.Id,
        order.OrderNumber,
        buyerHandle = handles[order.BuyerId].Handle,
        sellerHandle = handles[order.SellerId].Handle,
        viewerRole,
        title = order.Lines.Count > 0 ? order.Lines[0].Title : null,
        grandTotalRupiah = order.GrandTotal.Rupiah,
        escrowState = order.EscrowState.ToString(),
        order.CreatedAt,
    };

    /// <summary>
    /// The delivery address, as the viewer may see it. The buyer always sees their own. The
    /// seller sees it only once the order is paid: before that anyone could list an item,
    /// watch checkouts and harvest names, phone numbers and streets (QA S-02), and a cancelled
    /// order never ships, so it never reveals one either. Omitted from the JSON when null.
    /// </summary>
    private static object? ShipToFor(Order order, string viewerRole)
    {
        var revealed = string.Equals(viewerRole, "buyer", StringComparison.Ordinal)
            || order.EscrowState is not (EscrowState.AwaitingPayment or EscrowState.Cancelled);
        if (!revealed)
        {
            return null;
        }

        return new
        {
            order.ShipTo.RecipientName,
            order.ShipTo.Phone,
            order.ShipTo.Street,
            order.ShipTo.City,
            order.ShipTo.Province,
            order.ShipTo.PostalCode,
        };
    }

    private static object ToOrderDetail(
        Order order, string viewerRole, IReadOnlyDictionary<string, PublicProfile> handles, bool courierTracking) => new
    {
        order.Id,
        order.OrderNumber,
        buyerHandle = handles[order.BuyerId].Handle,
        sellerHandle = handles[order.SellerId].Handle,
        viewerRole,
        lines = order.Lines.Select(line => new
        {
            line.ListingId,
            line.Title,
            line.PhotoKey,
            unitPriceRupiah = line.UnitPrice.Rupiah,
            line.Quantity,
        }),
        shipTo = ShipToFor(order, viewerRole),
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

        // Two promises the order page makes, so it says exactly what will happen: the unpaid order's own
        // deadline (fifteen minutes, thirty for an auction win), and whether the courier is asked about the
        // parcel (when it is not, only the buyer's confirmation releases the money).
        paymentDueAt = order.PaymentDueAt,
        source = order.Source.ToString(),
        courierTracking,

        // What the seller will receive when escrow releases. Only the seller needs it; the buyer's view omits it.
        sellerPayoutRupiah = string.Equals(viewerRole, "seller", StringComparison.Ordinal) ? order.SellerPayout.Rupiah : (long?)null,
        trackingNumber = order.Shipment?.TrackingNumber,
        disputeOpen = order.Dispute is { Resolution: null },
    };

    private sealed record ShippingAddressRequest(
        string? RecipientName, string? Phone, string? Street, string? City, string? Province, string? PostalCode)
    {
        public ShippingAddressInput ToInput() => new(
            RecipientName ?? string.Empty, Phone ?? string.Empty, Street ?? string.Empty,
            City ?? string.Empty, Province ?? string.Empty, PostalCode ?? string.Empty);
    }

    private sealed record CheckoutRequest(
        string Source, string ReferenceId, ShippingAddressRequest? ShipTo, string CourierCode, int? Quantity = null);

    private sealed record MarkShippedRequest(
        string TrackingNumber, string PackedParcelPhotoKey, string TrackingLabelPhotoKey, DateOnly ShippedOn);

    private sealed record OpenDisputeRequest(string? Reason);
}
