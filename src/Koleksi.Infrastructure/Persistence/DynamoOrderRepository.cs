using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Stores orders, and the pointer that resolves an order number back to its id.</summary>
internal sealed partial class DynamoOrderRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IOrderRepository, IOrderSweepBackfill
{
    private const string OrderNumberAttr = "orderId";

    public async Task<Order?> FindAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = KeyOf(orderId) },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? OrderItem.ToDomain(item) : null;
    }

    public async Task<Order?> FindByOrderNumberAsync(string orderNumber, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderNumber);

        var pointer = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.OrderNumberPk(orderNumber)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.OrderNumberSk()),
                },
            },
            ct).ConfigureAwait(false);

        if (pointer.Item is not { Count: > 0 } pointerItem)
        {
            return null;
        }

        return await FindAsync(pointerItem.GetString(OrderNumberAttr), ct).ConfigureAwait(false);
    }

    public async Task AddManyAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orders);

        if (orders.Count == 0)
        {
            return;
        }

        var writes = new List<TransactWriteItem>(orders.Count * 2);
        foreach (var order in orders)
        {
            writes.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = OrderItem.ToItem(order),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            });

            writes.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.OrderNumberPk(order.OrderNumber)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.OrderNumberSk()),
                        [OrderNumberAttr] = Attr.S(order.Id),
                    },
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled)
            when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            // Order ids are UUIDv7 and order numbers are derived from them, so a genuine
            // collision is not the realistic cause — a retried checkout resubmitting the
            // same batch is. Either way, none of the checkout's orders exist half-written.
            throw new ConcurrencyConflictException("One or more orders in this checkout already exist.");
        }
    }

    public async Task UpdateAsync(Order order, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);

        var condition = "#v = :expected";
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["#v"] = OrderItem.VersionAttribute };
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal) { [":expected"] = Attr.N(expectedVersion) };

        try
        {
            if (order.Dispute is null)
            {
                await dynamo.PutItemAsync(
                    new PutItemRequest
                    {
                        TableName = options.TableName,
                        Item = OrderItem.ToItem(order),
                        ConditionExpression = condition,
                        ExpressionAttributeNames = names,
                        ExpressionAttributeValues = values,
                    },
                    ct).ConfigureAwait(false);
                return;
            }

            // The order has a dispute: its two pointer rows go in the same transaction, so there is never a dispute a person cannot
            // find or a pointer to one that does not exist. Writing them again on a later update is harmless (same item).
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            Put = new Put
                            {
                                TableName = options.TableName,
                                Item = OrderItem.ToItem(order),
                                ConditionExpression = condition,
                                ExpressionAttributeNames = names,
                                ExpressionAttributeValues = values,
                            },
                        },
                        .. DisputePointers(order).Select(pointer => new TransactWriteItem { Put = new Put { TableName = options.TableName, Item = pointer } }),
                    ],
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Order '{order.Id}' changed since it was read.");
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Order '{order.Id}' changed since it was read.");
        }
    }

    /// <summary>The pointer rows that make a dispute findable from each person in it: one for the buyer, one for the seller.</summary>
    internal static IEnumerable<Dictionary<string, AttributeValue>> DisputePointers(Order order)
    {
        foreach (var userId in new[] { order.BuyerId, order.SellerId })
        {
            yield return new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = Attr.S(DynamoTable.UserDisputePk(userId)),
                [DynamoTable.Sk] = Attr.S(DynamoTable.UserDisputeSk(order.Id)),
                [DynamoTable.EntityType] = Attr.S("DisputeRef"),
                ["orderId"] = Attr.S(order.Id),
            };
        }
    }

    /// <summary>The most pointers read for one person. A stop against a runaway cursor, far above any account.</summary>
    private const int DisputePointerCeiling = 5_000;

    private const int BatchGetLimit = 100;

    public async Task<IReadOnlyList<Order>> ListDisputedForUserAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var orderIds = new List<string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = "#pk = :pk",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#pk"] = DynamoTable.Pk },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.UserDisputePk(userId)),
                    },
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            orderIds.AddRange((response.Items ?? []).Select(item => item.GetString("orderId")));
            start = response.LastEvaluatedKey is { Count: > 0 } key && orderIds.Count < DisputePointerCeiling ? key : null;
        }
        while (start is not null);

        var found = new List<Order>(orderIds.Count);
        foreach (var chunk in orderIds.Distinct(StringComparer.Ordinal).Chunk(BatchGetLimit))
        {
            var request = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [options.TableName] = new KeysAndAttributes { Keys = [.. chunk.Select(KeyOf)] },
            };

            // BatchGetItem may answer part of a request; the rest comes back as UnprocessedKeys. Bounded, so it cannot spin.
            for (var attempt = 0; attempt < 5 && request.Count > 0; attempt++)
            {
                var response = await dynamo.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = request }, ct).ConfigureAwait(false);
                found.AddRange((response.Responses?.GetValueOrDefault(options.TableName) ?? []).Select(OrderItem.ToDomain));
                request = response.UnprocessedKeys is { Count: > 0 } rest ? rest : [];
            }
        }

        return [.. found.OrderByDescending(order => order.CreatedAt).ThenBy(order => order.Id, StringComparer.Ordinal)];
    }

    public async Task UpdateWithPayoutAsync(Order order, long expectedVersion, Payout payout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(payout);

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem
                        {
                            Put = new Put
                            {
                                TableName = options.TableName,
                                Item = OrderItem.ToItem(order),
                                ConditionExpression = "#v = :expected",
                                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["#v"] = OrderItem.VersionAttribute,
                                },
                                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                                {
                                    [":expected"] = Attr.N(expectedVersion),
                                },
                            },
                        },
                        new TransactWriteItem
                        {
                            Put = new Put
                            {
                                TableName = options.TableName,
                                Item = PayoutItem.ToItem(payout),

                                // One payout per order, whatever happens twice.
                                ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                            },
                        },
                    ],
                },
                ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Order '{order.Id}' changed since it was read, or its payout already exists.");
        }
    }

    public Task<Page<Order>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct) =>
        QueryIndexAsync(
            DynamoTable.BuyerIndex, DynamoTable.BuyerPk, DynamoTable.BuyerPkValue(buyerId),
            DynamoTable.BuyerSk, cursor, limit, ct);

    public Task<Page<Order>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct) =>
        QueryIndexAsync(
            DynamoTable.SellerIndex, DynamoTable.SellerPk, DynamoTable.SellerPkValue(sellerId),
            DynamoTable.SellerSk, cursor, limit, ct);

    /// <summary>Delivered orders whose auto-release deadline has passed.</summary>
    /// <remarks>
    /// A query on the sparse close-time index (see <see cref="DynamoTable.OrderDuePkValue"/>), never a
    /// scan. Orders written before that index entry existed carry no key and are not found here.
    /// </remarks>
    public Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct) =>
        ListDueAsync(nameof(EscrowState.Delivered), asOf, limit, ct);

    /// <summary>Unpaid orders whose payment window has elapsed.</summary>
    /// <remarks>Same index and caveats as <see cref="ListDueForAutoReleaseAsync"/>.</remarks>
    public Task<IReadOnlyList<Order>> ListPaymentExpiredAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct) =>
        ListDueAsync(nameof(EscrowState.AwaitingPayment), asOf, limit, ct);

    /// <summary>Shipped parcels whose next courier check is due.</summary>
    /// <remarks>Same index and caveats as <see cref="ListDueForAutoReleaseAsync"/>.</remarks>
    public Task<IReadOnlyList<Order>> ListDueForTrackingAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct) =>
        ListDueAsync(OrderItem.TrackingState, asOf, limit, ct);

    private async Task<IReadOnlyList<Order>> ListDueAsync(
        string state, DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Fan out over the shards and merge, exactly as the auction close sweep does: sequential
        // queries would make the latency the sum of four round trips for no reason.
        var perShard = Math.Max(1, limit / DynamoTable.AuctionShardCount);
        var queries = new Task<QueryResponse>[DynamoTable.AuctionShardCount];

        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            queries[shard] = dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,

                    // The index projects ALL attributes, which the order needs to be rehydrated.
                    IndexName = DynamoTable.AuctionEndIndex,
                    KeyConditionExpression =
                        $"{DynamoTable.AuctionEndPk} = :pk AND {DynamoTable.AuctionEndSk} <= :now",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.OrderDuePkValue(state, shard)),

                        // Inclusive of the whole tick, for the reason documented on EndsAtUpperBound.
                        [":now"] = Attr.S(DynamoTable.EndsAtUpperBound(asOf)),
                    },
                    Limit = perShard,

                    // Longest overdue first.
                    ScanIndexForward = true,
                },
                ct);
        }

        var responses = await Task.WhenAll(queries).ConfigureAwait(false);

        var due = new List<(DateTimeOffset DueAt, Order Order)>();
        foreach (var response in responses)
        {
            foreach (var item in response.Items ?? [])
            {
                var order = OrderItem.ToDomain(item);
                due.Add((OrderItem.DueEntryFor(order)?.DueAt ?? order.CreatedAt, order));
            }
        }

        // Back into one most-overdue-first order, then the caller's limit.
        due.Sort((left, right) => left.DueAt.CompareTo(right.DueAt));
        return due.Take(limit).Select(entry => entry.Order).ToList();
    }

    private async Task<Page<Order>> QueryIndexAsync(
        string indexName, string pkAttribute, string pkValue, string skAttribute,
        string? cursor, int limit, CancellationToken ct)
    {
        var response = await dynamo.QueryPageAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = indexName,
                KeyConditionExpression = $"{pkAttribute} = :pk AND begins_with({skAttribute}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(pkValue),
                    [":prefix"] = Attr.S(DynamoTable.OrderIndexPrefix()),
                },
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                Limit = limit,
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var orders = new List<Order>(response.Items?.Count ?? 0);
        foreach (var item in response.Items ?? [])
        {
            orders.Add(OrderItem.ToDomain(item));
        }

        return new Page<Order>(orders, DynamoCursor.Encode(response.LastEvaluatedKey), orders.Count);
    }

    private static Dictionary<string, AttributeValue> KeyOf(string orderId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(orderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OrderMetaSk()),
        };
}
