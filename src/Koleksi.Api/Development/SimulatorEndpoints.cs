using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Development;

/// <summary>
/// The controls behind the simulator page. They stand in for the two outside parties that are
/// not connected yet, the payment gateway ("this order was paid") and the courier ("this parcel moved, this parcel
/// arrived"), so the whole flow can be walked through by hand. Everything else (checkout, stock holds, deadlines,
/// shipping proof, escrow, release, payouts, warnings) is the real code.
/// </summary>
/// <remarks>
/// <para>
/// Three locks: the routes exist only when `Koleksi:Simulators:Enabled` is on (and the host will not start with
/// that on anywhere but a developer's machine or the dev stage), the caller must be signed in, and must be a party
/// to the order. There is no shared secret in
/// the browser: the caller's own session is what authorises, and a stranger gets the same answer a missing order
/// gets. Payment goes through the same handler a verified gateway notification does (only the signature check is
/// skipped), and the courier moves the order through the same <see cref="DeliveryProgress"/> the sweep and the
/// delivery webhook use, so nothing here can do what the real path could not.
/// </para>
/// </remarks>
internal static class SimulatorEndpoints
{
    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/dev/simulator").WithTags("Simulator");

        group.MapGet("", ListAsync).WithName("SimulatorList");
        group.MapPost("/orders/{orderId}/payment", PayAsync).WithName("SimulatorPay");
        group.MapPost("/orders/{orderId}/courier", CourierAsync).WithName("SimulatorCourier");
        group.MapPost("/sweep", SweepAsync).WithName("SimulatorSweep");

        return app;
    }

    private static async Task<IResult> ListAsync(
        ICurrentUser user, IOrderRepository orders, IClock clock, IConfiguration configuration, CancellationToken ct)
    {
        var userId = user.RequireUserId();
        var bought = await orders.ListForBuyerAsync(userId, null, 30, ct).ConfigureAwait(false);
        var sold = await orders.ListForSellerAsync(userId, null, 30, ct).ConfigureAwait(false);

        var rows = bought.Items.Select(order => (Order: order, Role: "buyer"))
            .Concat(sold.Items.Select(order => (Order: order, Role: "seller")))
            .Where(row => row.Order.EscrowState is not (EscrowState.Cancelled or EscrowState.Refunded))
            .OrderByDescending(row => row.Order.CreatedAt)
            .Take(40)
            .Select(row => new
            {
                id = row.Order.Id,
                orderNumber = row.Order.OrderNumber,
                role = row.Role,
                title = row.Order.Lines.Count > 0 ? row.Order.Lines[0].Title : null,
                quantity = row.Order.Lines.Sum(line => line.Quantity),
                grandTotalRupiah = row.Order.GrandTotal.Rupiah,
                escrowState = row.Order.EscrowState.ToString(),
                source = row.Order.Source.ToString(),
                createdAt = row.Order.CreatedAt,
                paymentDueAt = row.Order.EscrowState == EscrowState.AwaitingPayment ? row.Order.PaymentDueAt : (DateTimeOffset?)null,
                autoReleaseAt = row.Order.AutoReleaseAt,
                trackingNumber = row.Order.Shipment?.TrackingNumber,

                // What each button may do right now. The server decides; the page only draws it.
                canPay = row.Role == "buyer" && row.Order.EscrowState == EscrowState.AwaitingPayment
                    && row.Order.PaymentDueAt > clock.UtcNow,
                canMoveCourier = row.Order.EscrowState is EscrowState.Shipped or EscrowState.InTransit,
                needsSellerToShip = row.Role == "seller" && row.Order.EscrowState == EscrowState.Paid,
                canConfirm = row.Role == "buyer"
                    && row.Order.EscrowState is EscrowState.Shipped or EscrowState.InTransit or EscrowState.Delivered,
            })
            .ToList();

        return Results.Ok(new
        {
            serverNow = clock.UtcNow,
            autoReleaseAfterDays = Order.AutoReleaseAfterDelivery.TotalDays,
            orders = rows,
        });
    }

    private static async Task<IResult> PayAsync(
        string orderId,
        ICurrentUser user,
        IOrderRepository orders,
        ConfirmOrderPaymentHandler payments,
        CancellationToken ct)
    {
        var order = await FindPartyOrderAsync(orders, user.RequireUserId(), orderId, ct).ConfigureAwait(false);
        if (order is null || !string.Equals(order.BuyerId, user.RequireUserId(), StringComparison.Ordinal))
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        if (order.EscrowState != EscrowState.AwaitingPayment && order.EscrowState != EscrowState.Cancelled)
        {
            return Results.Ok(new { escrowState = order.EscrowState.ToString() });
        }

        // The same call a verified settled notification makes; only the signature check is skipped.
        await payments.ApplyTrustedAsync(
            new PaymentNotification(order.OrderNumber, PaymentStatus.Settled, order.GrandTotal, $"SIM-{order.Id}", $"sim-{order.Id}"),
            ct).ConfigureAwait(false);

        var after = await orders.FindAsync(order.Id, ct).ConfigureAwait(false);
        return Results.Ok(new { escrowState = after?.EscrowState.ToString() });
    }

    private static async Task<IResult> CourierAsync(
        string orderId,
        CourierRequest request,
        ICurrentUser user,
        IOrderRepository orders,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var order = await FindPartyOrderAsync(orders, user.RequireUserId(), orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return Results.NotFound(new { code = "order.not_found" });
        }

        var state = request.State switch
        {
            "in_transit" => DeliveryState.InTransit,
            "delivered" => DeliveryState.Delivered,
            _ => DeliveryState.Unknown,
        };
        if (state == DeliveryState.Unknown)
        {
            return Results.BadRequest(new { code = "simulator.invalid_state" });
        }

        if (order.EscrowState is EscrowState.Paid or EscrowState.AwaitingPayment)
        {
            // The courier cannot report a parcel the seller has not shipped yet.
            return Results.Conflict(new { code = "shipping.not_shipped_yet" });
        }

        var expectedVersion = order.Version;
        if (DeliveryProgress.Apply(order, state, clock))
        {
            await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
        }

        return Results.Ok(new
        {
            escrowState = order.EscrowState.ToString(),
            autoReleaseAt = order.AutoReleaseAt,
        });
    }

    private static async Task<IResult> SweepAsync(SweepOrchestrator sweeps, CancellationToken ct) =>
        Results.Ok(await sweeps.RunAsync(ct).ConfigureAwait(false));

    /// <summary>The order, or <c>null</c> when it is missing or the caller is neither its buyer nor its seller.</summary>
    private static async Task<Order?> FindPartyOrderAsync(
        IOrderRepository orders, string userId, string orderId, CancellationToken ct)
    {
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        return order is not null
            && (string.Equals(order.BuyerId, userId, StringComparison.Ordinal)
                || string.Equals(order.SellerId, userId, StringComparison.Ordinal))
            ? order
            : null;
    }

    private sealed record CourierRequest(string? State);
}
