using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IOrderRepository"/> that honours the version condition.</summary>
public sealed class FakeOrderRepository : FakeAdapter, IOrderRepository
{
    private readonly Dictionary<string, Order> _orders = new(StringComparer.Ordinal);

    public void Seed(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        _orders[order.Id] = order;
    }

    public Task<Order?> FindAsync(string orderId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(_orders.TryGetValue(orderId, out var order) ? Copy(order) : null);
    }

    public Task<Order?> FindByOrderNumberAsync(string orderNumber, CancellationToken ct)
    {
        ThrowIfFailing();
        var found = _orders.Values.FirstOrDefault(o => o.OrderNumber == orderNumber);
        return Task.FromResult(found is null ? null : Copy(found));
    }

    public Task AddManyAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(orders);

        foreach (var order in orders)
        {
            if (_orders.ContainsKey(order.Id))
            {
                throw new ConcurrencyConflictException($"Order {order.Id} already exists.");
            }
        }

        foreach (var order in orders)
        {
            _orders[order.Id] = Copy(order);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Order order, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(order);

        if (!_orders.TryGetValue(order.Id, out var stored) || stored.Version != expectedVersion)
        {
            throw new ConcurrencyConflictException($"Order {order.Id} changed since it was read.");
        }

        _orders[order.Id] = Copy(order);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Order>> ListDisputedForUserAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<Order>>(
            [.. _orders.Values.Where(o => o.Dispute is not null && (o.BuyerId == userId || o.SellerId == userId)).OrderByDescending(o => o.CreatedAt).Select(Copy)]);
    }

    public Task<Page<Order>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        var items = _orders.Values.Where(o => o.BuyerId == buyerId)
            .OrderByDescending(o => o.CreatedAt).Take(limit).Select(Copy).ToList();
        return Task.FromResult(new Page<Order>(items, null, items.Count));
    }

    public Task<Page<Order>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        var items = _orders.Values.Where(o => o.SellerId == sellerId)
            .OrderByDescending(o => o.CreatedAt).Take(limit).Select(Copy).ToList();
        return Task.FromResult(new Page<Order>(items, null, items.Count));
    }

    public Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        // Mirrors the sparse index: only Delivered orders carry an entry, due at AutoReleaseAt.
        IReadOnlyList<Order> due = _orders.Values
            .Where(o => o.EscrowState == EscrowState.Delivered && o.AutoReleaseAt is { } at && at <= asOf)
            .OrderBy(o => o.AutoReleaseAt)
            .Take(limit).Select(Copy).ToList();
        return Task.FromResult(due);
    }

    public Task<IReadOnlyList<Order>> ListPaymentExpiredAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        // Mirrors the sparse index: only AwaitingPayment orders carry an entry, due when the window ends.
        IReadOnlyList<Order> due = _orders.Values
            .Where(o => o.EscrowState == EscrowState.AwaitingPayment && o.PaymentDueAt <= asOf)
            .OrderBy(o => o.PaymentDueAt)
            .Take(limit).Select(Copy).ToList();
        return Task.FromResult(due);
    }

    /// <summary>The payouts written together with released orders (the transactional half of release).</summary>
    public FakePayoutRepository Payouts { get; } = new();

    public async Task UpdateWithPayoutAsync(Order order, long expectedVersion, Payout payout, CancellationToken ct)
    {
        ThrowIfFailing();

        // Both or neither: the payout is added only if the order write passes its version check.
        await UpdateAsync(order, expectedVersion, ct);
        Payouts.AddForRelease(payout);
    }

    public Task<IReadOnlyList<Order>> ListDueForTrackingAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();

        // Mirrors the sparse index: only shipped parcels carry an entry, due at NextTrackingCheckAt.
        IReadOnlyList<Order> due = _orders.Values
            .Where(o => o.NextTrackingCheckAt is { } at && at <= asOf)
            .OrderBy(o => o.NextTrackingCheckAt)
            .Take(limit).Select(Copy).ToList();
        return Task.FromResult(due);
    }

    private static Order Copy(Order order) => Order.Rehydrate(
        order.Id, order.OrderNumber, order.BuyerId, order.SellerId, order.Lines, order.ShipTo, order.Courier,
        order.EscrowState, order.Shipment, order.Dispute, order.CreatedAt, order.PaidAt, order.ShippedAt,
        order.DeliveredAt, order.ReleasedAt, order.Version, order.FeePercentSnapshot,
        order.Source, order.PaymentDueAt, order.TrackingCheckedAt, order.SourceReference);
}
