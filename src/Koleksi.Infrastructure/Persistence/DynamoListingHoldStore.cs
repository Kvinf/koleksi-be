using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IListingHoldStore"/> as one small row per unit of a listing ("slot"), taken and released
/// with conditional writes.
/// </summary>
/// <remarks>
/// <para>
/// A listing with <c>n</c> units owns slots <c>0 .. n-1</c> under its hold partition. A slot is absent
/// or expired (free), <c>held</c> (an order reserved it) or <c>sold</c> (that order was paid, and the
/// row is permanent). Reserving a unit is a conditional put on one slot that succeeds only when the slot
/// is absent or its hold has lapsed, so two buyers racing for the last unit cannot both pass the
/// condition: stock can never be oversold, and no counter has to be kept in step with anything.
/// </para>
/// <para>
/// A multi-unit reservation is one transaction over its slots, all or nothing. The lapse is checked
/// against the clock inside the condition, not left to DynamoDB's TTL, because TTL removes rows up to a
/// couple of days late. The same attribute is also the TTL attribute, so the table still tidies itself;
/// a sold slot carries no expiry and is never removed.
/// </para>
/// </remarks>
internal sealed class DynamoListingHoldStore(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
    : IListingHoldStore
{
    private const string SlotPrefix = "SLOT#";
    private const string HolderAttr = "holderId";
    private const string OrderAttr = "orderId";
    private const string TtlAttr = "expiresAt";
    private const string FirstHeldAtAttr = "firstHeldAt";
    private const string StateAttr = "slotState";
    private const string SoldAtAttr = "soldAt";
    private const string Held = "held";
    private const string Sold = "sold";

    /// <summary>How often a reservation re-reads the slots after losing a race before giving up.</summary>
    private const int MaxAttempts = 4;

    public Task<HoldResult> TryHoldAsync(string listingId, string buyerId, string orderId, CancellationToken ct) =>
        TryReserveAsync(listingId, 1, buyerId, orderId, 1, clock.UtcNow + IListingHoldStore.Duration, ct);

    public async Task<HoldResult> TryReserveAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, DateTimeOffset heldUntil, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentOutOfRangeException.ThrowIfLessThan(stock, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(stock, IListingHoldStore.MaxStock);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quantity, stock);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = clock.UtcNow;
            var slots = await ReadSlotsAsync(listingId, ct).ConfigureAwait(false);

            // Free for a new buyer: absent, or a hold that has lapsed. Free for THIS buyer too: their own
            // live holds, which a repeat checkout replaces.
            var states = new SlotState?[stock];
            foreach (var slot in slots)
            {
                if (slot.Index < stock)
                {
                    states[slot.Index] = slot;
                }
            }

            var ownLive = slots.Where(s => s.IsLiveHeldBy(buyerId, now)).ToList();
            var free = new List<int>();
            var freeForOthers = 0;
            for (var index = 0; index < stock; index++)
            {
                var slot = states[index];
                if (slot is null || slot.IsFreeAt(now))
                {
                    free.Add(index);
                    freeForOthers++;
                }
                else if (slot.IsLiveHeldBy(buyerId, now))
                {
                    free.Add(index);
                }
            }

            if (free.Count < quantity)
            {
                return new HoldResult(Acquired: false, ReplacedOrderId: null, Available: freeForOthers);
            }

            // Prefer the buyer's own slots so a repeat checkout rewrites rather than churns.
            var chosen = free.OrderBy(index => states[index]?.IsLiveHeldBy(buyerId, now) == true ? 0 : 1)
                .ThenBy(index => index)
                .Take(quantity)
                .OrderBy(index => index)
                .ToList();

            var firstHeldAt = ownLive.Count > 0 ? ownLive.Min(s => s.FirstHeldAt) : now;
            var until = Earlier(heldUntil, firstHeldAt + IListingHoldStore.MaxTotalDuration);
            if (until <= now)
            {
                return new HoldResult(Acquired: false, ReplacedOrderId: null, Available: freeForOthers);
            }

            var drop = ownLive.Where(s => !chosen.Contains(s.Index)).Select(s => s.Index).ToList();

            if (!await TryWriteAsync(listingId, buyerId, orderId, chosen, drop, until, firstHeldAt, now, ct).ConfigureAwait(false))
            {
                continue;
            }

            var replacedOrders = ownLive.Select(s => s.OrderId).Where(o => o is not null)
                .Cast<string>().Distinct(StringComparer.Ordinal).ToList();

            // Units that were free for others and are now this order's.
            var tookFromOthers = chosen.Count(index => states[index] is null || states[index]!.IsFreeAt(now));

            return new HoldResult(
                Acquired: true,
                replacedOrders.FirstOrDefault(),
                until,
                replacedOrders,
                Available: Math.Max(0, freeForOthers - tookFromOthers));
        }

        return new HoldResult(Acquired: false, ReplacedOrderId: null);
    }

    public async Task<StockAvailability> GetAvailabilityAsync(string listingId, int stock, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var now = clock.UtcNow;
        var slots = await ReadSlotsAsync(listingId, ct).ConfigureAwait(false);

        var sold = 0;
        var held = 0;
        var highest = -1;
        foreach (var slot in slots)
        {
            if (slot.Sold)
            {
                sold++;
                highest = Math.Max(highest, slot.Index);
            }
            else if (!slot.IsFreeAt(now))
            {
                held++;
                highest = Math.Max(highest, slot.Index);
            }
        }

        return new StockAvailability(stock, sold, held, highest);
    }

    public async Task<int> CommitSaleAsync(
        string listingId, int stock, string buyerId, string orderId, int quantity, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = clock.UtcNow;
            var slots = await ReadSlotsAsync(listingId, ct).ConfigureAwait(false);

            var owned = slots.Count(s => s.Sold && s.OrderId == orderId);
            var toConvert = slots.Where(s => !s.Sold && s.OrderId == orderId).Select(s => s.Index).ToList();
            var missing = quantity - owned - toConvert.Count;

            // Units the order's hold lost (it lapsed and the slot was reused or removed): claim any that are
            // still free, so a late but genuine payment is not turned away while stock remains.
            var claim = new List<int>();
            if (missing > 0)
            {
                var taken = slots.Where(s => s.Sold || !s.IsFreeAt(now)).Select(s => s.Index).ToHashSet();
                for (var index = 0; index < stock && claim.Count < missing; index++)
                {
                    if (!taken.Contains(index))
                    {
                        claim.Add(index);
                    }
                }
            }

            var converted = 0;
            var raced = false;

            foreach (var index in toConvert)
            {
                if (await TrySellAsync(listingId, index, buyerId, orderId, mustOwn: true, now, ct).ConfigureAwait(false))
                {
                    converted++;
                }
                else
                {
                    raced = true;
                }
            }

            foreach (var index in claim)
            {
                if (await TrySellAsync(listingId, index, buyerId, orderId, mustOwn: false, now, ct).ConfigureAwait(false))
                {
                    converted++;
                }
                else
                {
                    raced = true;
                }
            }

            if (!raced || attempt == MaxAttempts)
            {
                return owned + converted;
            }
        }

        return 0;
    }

    public async Task<bool> IsHeldAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        // Consistent: the answer gates a delisting, and an eventually consistent read could miss a
        // hold taken a moment ago. The lapse is judged against our clock, not DynamoDB's TTL sweep.
        var now = clock.UtcNow;
        var slots = await ReadSlotsAsync(listingId, ct).ConfigureAwait(false);
        return slots.Any(slot => !slot.Sold && !slot.IsFreeAt(now));
    }

    public async Task ReleaseAsync(string listingId, string buyerId, string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var slots = await ReadSlotsAsync(listingId, ct).ConfigureAwait(false);
        foreach (var slot in slots.Where(s => !s.Sold && s.OrderId == orderId && s.HolderId == buyerId))
        {
            try
            {
                await dynamo.DeleteItemAsync(
                    new DeleteItemRequest
                    {
                        TableName = options.TableName,
                        Key = KeyOf(listingId, slot.Index),

                        // Only this order's own hold: someone else may have taken the slot since, and a
                        // sold unit is never given back.
                        ConditionExpression = "#holder = :buyer AND #order = :order AND #state = :held",
                        ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["#holder"] = HolderAttr,
                            ["#order"] = OrderAttr,
                            ["#state"] = StateAttr,
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [":buyer"] = Attr.S(buyerId),
                            [":order"] = Attr.S(orderId),
                            [":held"] = Attr.S(Held),
                        },
                    },
                    ct).ConfigureAwait(false);
            }
            catch (ConditionalCheckFailedException)
            {
                // Already gone, lapsed, sold, or someone else's now. Nothing of ours to release.
            }
        }
    }

    // ─── Writes ─────────────────────────────────────────────────────────────────

    private async Task<bool> TryWriteAsync(
        string listingId,
        string buyerId,
        string orderId,
        List<int> take,
        List<int> drop,
        DateTimeOffset until,
        DateTimeOffset firstHeldAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var writes = new List<TransactWriteItem>(take.Count + drop.Count);

        foreach (var index in take)
        {
            writes.Add(new TransactWriteItem { Put = PutHold(listingId, index, buyerId, orderId, until, firstHeldAt, now) });
        }

        foreach (var index in drop)
        {
            writes.Add(new TransactWriteItem
            {
                Delete = new Delete
                {
                    TableName = options.TableName,
                    Key = KeyOf(listingId, index),
                    ConditionExpression = "#holder = :buyer AND #state = :held",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#holder"] = HolderAttr,
                        ["#state"] = StateAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":buyer"] = Attr.S(buyerId),
                        [":held"] = Attr.S(Held),
                    },
                },
            });
        }

        try
        {
            if (writes.Count == 1 && writes[0].Put is { } single)
            {
                await dynamo.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = single.TableName,
                        Item = single.Item,
                        ConditionExpression = single.ConditionExpression,
                        ExpressionAttributeNames = single.ExpressionAttributeNames,
                        ExpressionAttributeValues = single.ExpressionAttributeValues,
                    },
                    ct).ConfigureAwait(false);
            }
            else
            {
                await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            return false;
        }
        catch (TransactionConflictException)
        {
            return false;
        }
    }

    private Put PutHold(
        string listingId, int index, string buyerId, string orderId, DateTimeOffset until, DateTimeOffset firstHeldAt, DateTimeOffset now) =>
        new()
        {
            TableName = options.TableName,
            Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = Attr.S(DynamoTable.ListingHoldPk(listingId)),
                [DynamoTable.Sk] = Attr.S(SlotSk(index)),
                [DynamoTable.EntityType] = Attr.S("StockSlot"),
                [HolderAttr] = Attr.S(buyerId),
                [OrderAttr] = Attr.S(orderId),
                [StateAttr] = Attr.S(Held),
                [TtlAttr] = Attr.N(DynamoTable.ToTtl(until)),
                [FirstHeldAtAttr] = Attr.N(firstHeldAt.ToUnixTimeSeconds()),
            },

            // A slot can be taken when nothing is there, when its hold has lapsed (and it was not sold:
            // a sold row has no expiry, so the second clause is false for it), or when it is already this
            // buyer's own.
            ConditionExpression =
                $"attribute_not_exists({DynamoTable.Pk}) OR (attribute_exists(#exp) AND #exp <= :now) OR #holder = :buyer",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#exp"] = TtlAttr,
                ["#holder"] = HolderAttr,
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":now"] = Attr.N(now.ToUnixTimeSeconds()),
                [":buyer"] = Attr.S(buyerId),
            },
        };

    /// <summary>
    /// Turns one unit into a sold one. With <paramref name="mustOwn"/> it is the order's own held slot;
    /// without, it is a free slot claimed because the hold behind the payment had lapsed.
    /// </summary>
    private async Task<bool> TrySellAsync(
        string listingId, int index, string buyerId, string orderId, bool mustOwn, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.ListingHoldPk(listingId)),
                        [DynamoTable.Sk] = Attr.S(SlotSk(index)),
                        [DynamoTable.EntityType] = Attr.S("StockSlot"),
                        [HolderAttr] = Attr.S(buyerId),
                        [OrderAttr] = Attr.S(orderId),
                        [StateAttr] = Attr.S(Sold),
                        [SoldAtAttr] = Attr.N(now.ToUnixTimeSeconds()),
                        // No TTL attribute: a sold unit is permanent.
                    },
                    ConditionExpression = mustOwn
                        ? "#order = :order AND #state = :held"
                        : $"attribute_not_exists({DynamoTable.Pk}) OR (attribute_exists(#exp) AND #exp <= :now)",
                    ExpressionAttributeNames = mustOwn
                        ? new Dictionary<string, string>(StringComparer.Ordinal) { ["#order"] = OrderAttr, ["#state"] = StateAttr }
                        : new Dictionary<string, string>(StringComparer.Ordinal) { ["#exp"] = TtlAttr },
                    ExpressionAttributeValues = mustOwn
                        ? new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [":order"] = Attr.S(orderId),
                            [":held"] = Attr.S(Held),
                        }
                        : new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [":now"] = Attr.N(now.ToUnixTimeSeconds()),
                        },
                },
                ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    // ─── Reads ──────────────────────────────────────────────────────────────────

    private async Task<List<SlotState>> ReadSlotsAsync(string listingId, CancellationToken ct)
    {
        var slots = new List<SlotState>();
        Dictionary<string, AttributeValue>? startKey = null;

        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.ListingHoldPk(listingId)),
                        [":prefix"] = Attr.S(SlotPrefix),
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            foreach (var item in response.Items ?? [])
            {
                if (TryParse(item) is { } slot)
                {
                    slots.Add(slot);
                }
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (startKey is not null);

        return slots;
    }

    private SlotState? TryParse(Dictionary<string, AttributeValue> item)
    {
        var sk = item.GetStringOrNull(DynamoTable.Sk);
        if (sk is null
            || !sk.StartsWith(SlotPrefix, StringComparison.Ordinal)
            || !int.TryParse(sk.AsSpan(SlotPrefix.Length), out var index))
        {
            return null;
        }

        var state = item.GetStringOrNull(StateAttr);
        var expires = item.GetLongOrDefault(TtlAttr);
        return new SlotState(
            index,
            Sold: string.Equals(state, Sold, StringComparison.Ordinal),
            item.GetStringOrNull(HolderAttr),
            item.GetStringOrNull(OrderAttr),
            expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires) : null,
            item.GetLongOrDefault(FirstHeldAtAttr) is > 0 and var first
                ? DateTimeOffset.FromUnixTimeSeconds(first)
                : (expires > 0 ? DateTimeOffset.FromUnixTimeSeconds(expires) - IListingHoldStore.Duration : clock.UtcNow));
    }

    private static string SlotSk(int index) => $"{SlotPrefix}{index:D3}";

    private static Dictionary<string, AttributeValue> KeyOf(string listingId, int index) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingHoldPk(listingId)),
            [DynamoTable.Sk] = Attr.S(SlotSk(index)),
        };

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private sealed record SlotState(
        int Index, bool Sold, string? HolderId, string? OrderId, DateTimeOffset? ExpiresAt, DateTimeOffset FirstHeldAt)
    {
        /// <summary>A slot nobody holds: its hold has run out (a sold slot never frees).</summary>
        public bool IsFreeAt(DateTimeOffset now) => !Sold && (ExpiresAt is null || ExpiresAt <= now);

        public bool IsLiveHeldBy(string buyerId, DateTimeOffset now) =>
            !Sold && ExpiresAt > now && string.Equals(HolderId, buyerId, StringComparison.Ordinal);
    }
}
