using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Application.Features.Orders;

/// <summary>
/// The hold-taking step shared by single-item and cart checkout: reserve every listing an
/// order covers, or refuse the whole checkout.
/// </summary>
/// <remarks>
/// See <see cref="IListingHoldStore"/> for why holds exist. This is only the orchestration: take
/// them in order, undo the ones already taken if a later one is refused, and clear out the
/// unpaid order a repeat checkout by the same buyer supersedes.
/// </remarks>
internal static class OrderHolds
{
    /// <summary>
    /// Reserves the units of every line in <paramref name="orders"/> until each order's payment deadline.
    /// All or nothing: if any line cannot be fully reserved, the ones already taken are given back.
    /// </summary>
    /// <param name="stockByListing">How many units each listing has in all (what the seller stocked).</param>
    /// <returns>The earlier orders of the same buyer that these supersede.</returns>
    /// <exception cref="DomainException">
    /// <c>order.out_of_stock</c>: every unit is sold. <c>order.item_on_hold</c>: the free units are being paid
    /// for by other buyers right now.
    /// </exception>
    public static async Task<IReadOnlyList<string>> AcquireAsync(
        IListingHoldStore holds,
        string buyerId,
        IReadOnlyList<Order> orders,
        IReadOnlyDictionary<string, int> stockByListing,
        CancellationToken ct)
    {
        var taken = new List<(string ListingId, string OrderId)>();
        var replaced = new List<string>();

        foreach (var order in orders)
        {
            foreach (var line in order.Lines)
            {
                var stock = stockByListing.TryGetValue(line.ListingId, out var known) ? known : 1;
                var result = await holds.TryReserveAsync(
                    line.ListingId, stock, buyerId, order.Id, line.Quantity, order.PaymentDueAt, ct).ConfigureAwait(false);

                if (!result.Acquired)
                {
                    await ReleaseAsync(holds, buyerId, taken, ct).ConfigureAwait(false);

                    // Not enough free units. If nobody is paying for any, the stock itself is short (sold
                    // out, or fewer left than asked for); if others are paying, their units may come back.
                    var usage = await holds.GetAvailabilityAsync(line.ListingId, stock, ct).ConfigureAwait(false);
                    throw usage.SoldOut || usage.Held == 0
                        ? DomainException.For(
                            "order.out_of_stock",
                            usage.Available > 0
                                ? $"Only {usage.Available} left. Choose a smaller quantity."
                                : "This item is out of stock.")
                        : DomainException.For(
                            "order.item_on_hold",
                            "Other buyers are paying for the last units right now. If they do not pay, the stock is back in 15 minutes.");
                }

                taken.Add((line.ListingId, order.Id));
                foreach (var previous in result.ReplacedOrderIds ?? [])
                {
                    if (!replaced.Contains(previous, StringComparer.Ordinal))
                    {
                        replaced.Add(previous);
                    }
                }
            }
        }

        return replaced;
    }

    /// <summary>Gives back holds taken for orders that ended up not being created.</summary>
    public static Task ReleaseAsync(
        IListingHoldStore holds, string buyerId, IReadOnlyList<Order> orders, CancellationToken ct) =>
        ReleaseAsync(
            holds, buyerId, orders.SelectMany(order => order.Lines.Select(line => (line.ListingId, order.Id))).ToList(), ct);

    private static async Task ReleaseAsync(
        IListingHoldStore holds, string buyerId, List<(string ListingId, string OrderId)> taken, CancellationToken ct)
    {
        foreach (var (listingId, orderId) in taken)
        {
            await holds.ReleaseAsync(listingId, buyerId, orderId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the buyer's own earlier unpaid orders that a repeat checkout replaced, so one
    /// item never has two open orders from the same person. Best effort: an order that has
    /// meanwhile been paid, or changed under us, is left alone.
    /// </summary>
    public static async Task CancelReplacedAsync(
        IOrderRepository orders, IClock clock, string buyerId, IReadOnlyList<string> orderIds, CancellationToken ct)
    {
        foreach (var orderId in orderIds)
        {
            var previous = await orders.FindAsync(orderId, ct).ConfigureAwait(false);
            if (previous is null
                || !string.Equals(previous.BuyerId, buyerId, StringComparison.Ordinal)
                || previous.EscrowState != EscrowState.AwaitingPayment)
            {
                continue;
            }

            try
            {
                var version = previous.Version;
                previous.Cancel(clock);
                await orders.UpdateAsync(previous, version, ct).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is DomainException or ConcurrencyConflictException)
            {
                // Paid or edited between the read and the write: no longer ours to cancel.
            }
        }
    }
}
