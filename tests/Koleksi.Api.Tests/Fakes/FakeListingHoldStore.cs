using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IListingHoldStore"/> with the same rules as the DynamoDB one: one slot per
/// unit, free when absent or lapsed, held by an order until its deadline, sold for good once paid, and a
/// same-buyer renewal never reaching past <see cref="IListingHoldStore.MaxTotalDuration"/> after the hold
/// was first taken.
/// </summary>
public sealed class FakeListingHoldStore(IClock clock) : IListingHoldStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<int, Slot>> _slots = [];

    private sealed record Slot(string Buyer, string Order, DateTimeOffset? Until, DateTimeOffset FirstHeldAt, bool Sold);

    public Task<HoldResult> TryHoldAsync(string listingId, string buyerId, string orderId, CancellationToken ct) =>
        TryReserveAsync(listingId, 1, buyerId, orderId, 1, clock.UtcNow + IListingHoldStore.Duration, ct);

    public Task<HoldResult> TryReserveAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, DateTimeOffset heldUntil, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var slots = SlotsOf(listingId);

            var free = new List<int>();
            var freeForOthers = 0;
            var own = new List<int>();
            for (var index = 0; index < stock; index++)
            {
                if (!slots.TryGetValue(index, out var slot) || IsFree(slot, now))
                {
                    free.Add(index);
                    freeForOthers++;
                }
                else if (!slot.Sold && slot.Buyer == buyerId)
                {
                    free.Add(index);
                    own.Add(index);
                }
            }

            if (free.Count < quantity)
            {
                return Task.FromResult(new HoldResult(Acquired: false, ReplacedOrderId: null, Available: freeForOthers));
            }

            var firstHeldAt = own.Count > 0 ? own.Min(i => slots[i].FirstHeldAt) : now;
            var until = heldUntil;
            var cap = firstHeldAt + IListingHoldStore.MaxTotalDuration;
            if (until > cap)
            {
                until = cap;
            }

            if (until <= now)
            {
                return Task.FromResult(new HoldResult(Acquired: false, ReplacedOrderId: null, Available: freeForOthers));
            }

            var replaced = own.Select(i => slots[i].Order).Distinct().ToList();
            var chosen = free.OrderBy(i => own.Contains(i) ? 0 : 1).ThenBy(i => i).Take(quantity).ToList();
            var tookFromOthers = chosen.Count(i => !own.Contains(i));

            foreach (var index in own.Where(i => !chosen.Contains(i)))
            {
                slots.Remove(index);
            }

            foreach (var index in chosen)
            {
                slots[index] = new Slot(buyerId, orderId, until, firstHeldAt, Sold: false);
            }

            return Task.FromResult(new HoldResult(
                Acquired: true, replaced.FirstOrDefault(), until, replaced, Math.Max(0, freeForOthers - tookFromOthers)));
        }
    }

    public Task<StockAvailability> GetAvailabilityAsync(string listingId, int stock, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            int sold = 0, held = 0, highest = -1;
            foreach (var (index, slot) in SlotsOf(listingId))
            {
                if (slot.Sold)
                {
                    sold++;
                    highest = Math.Max(highest, index);
                }
                else if (!IsFree(slot, now))
                {
                    held++;
                    highest = Math.Max(highest, index);
                }
            }

            return Task.FromResult(new StockAvailability(stock, sold, held, highest));
        }
    }

    public Task<int> CommitSaleAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var slots = SlotsOf(listingId);

            foreach (var index in slots.Where(p => !p.Value.Sold && p.Value.Order == orderId).Select(p => p.Key).ToList())
            {
                slots[index] = new Slot(buyerId, orderId, null, slots[index].FirstHeldAt, Sold: true);
            }

            var owned = slots.Count(p => p.Value.Sold && p.Value.Order == orderId);
            for (var index = 0; index < stock && owned < quantity; index++)
            {
                if (!slots.TryGetValue(index, out var slot) || IsFree(slot, now))
                {
                    slots[index] = new Slot(buyerId, orderId, null, now, Sold: true);
                    owned++;
                }
            }

            return Task.FromResult(owned);
        }
    }

    public Task<bool> IsHeldAsync(string listingId, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            return Task.FromResult(SlotsOf(listingId).Values.Any(slot => !slot.Sold && !IsFree(slot, now)));
        }
    }

    public Task ReleaseAsync(string listingId, string buyerId, string orderId, CancellationToken ct)
    {
        lock (_gate)
        {
            var slots = SlotsOf(listingId);
            foreach (var index in slots
                .Where(p => !p.Value.Sold && p.Value.Buyer == buyerId && p.Value.Order == orderId)
                .Select(p => p.Key).ToList())
            {
                slots.Remove(index);
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsFree(Slot slot, DateTimeOffset now) => !slot.Sold && (slot.Until is null || slot.Until <= now);

    private Dictionary<int, Slot> SlotsOf(string listingId)
    {
        if (!_slots.TryGetValue(listingId, out var slots))
        {
            slots = [];
            _slots[listingId] = slots;
        }

        return slots;
    }
}
