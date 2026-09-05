using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Orders;

/// <summary>Attaches tracking and marks an order shipped, on behalf of the seller.</summary>
/// <remarks>The call site <c>ship.tsx</c>'s own <c>TODO(api)</c> comment already names.</remarks>
public sealed class MarkOrderShippedHandler(IOrderRepository orders, ICurrentUser currentUser, IClock clock)
{
    public async Task<FulfillOrderOutcome> HandleAsync(MarkOrderShippedCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var sellerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(command.OrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return FulfillOrderOutcome.NotFound;
        }

        var shipment = Shipment.Create(
            order.Courier.CourierCode, command.TrackingNumber,
            command.PackedParcelPhotoKey, command.TrackingLabelPhotoKey, command.ShippedOn);

        var expectedVersion = order.Version;
        order.MarkShipped(shipment, sellerId, clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        return FulfillOrderOutcome.Success(order);
    }
}

/// <summary>
/// Releases escrow by explicit buyer confirmation — "I received this, everything's fine".
/// </summary>
public sealed class ConfirmDeliveryHandler(IOrderRepository orders, ICurrentUser currentUser, IClock clock)
{
    public async Task<FulfillOrderOutcome> HandleAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var buyerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return FulfillOrderOutcome.NotFound;
        }

        if (!string.Equals(order.BuyerId, buyerId, StringComparison.Ordinal))
        {
            throw DomainException.For("order.forbidden", "Only the buyer can confirm receipt.");
        }

        var expectedVersion = order.Version;
        order.Release(confirmedByBuyer: true, clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        return FulfillOrderOutcome.Success(order);
    }
}

/// <summary>Opens a dispute on behalf of the buyer, freezing release while support reviews it.</summary>
public sealed class OpenOrderDisputeHandler(IOrderRepository orders, ICurrentUser currentUser, IClock clock)
{
    public async Task<FulfillOrderOutcome> HandleAsync(string orderId, string reason, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var buyerId = currentUser.RequireUserId();
        var order = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            return FulfillOrderOutcome.NotFound;
        }

        var expectedVersion = order.Version;
        order.OpenDispute(reason, buyerId, clock);
        await orders.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);

        return FulfillOrderOutcome.Success(order);
    }
}

public sealed record MarkOrderShippedCommand(
    string OrderId,
    string TrackingNumber,
    string PackedParcelPhotoKey,
    string TrackingLabelPhotoKey,
    DateOnly ShippedOn);

/// <param name="Found"><c>false</c> when the order does not exist.</param>
/// <param name="EscrowState">The order's state after the action.</param>
public sealed record FulfillOrderOutcome(bool Found, EscrowState? EscrowState)
{
    public static FulfillOrderOutcome NotFound { get; } = new(false, null);

    public static FulfillOrderOutcome Success(Order order) => new(true, order.EscrowState);
}
