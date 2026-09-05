using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Stores orders, and the pointer that resolves an order number back to its id.</summary>
internal sealed class DynamoOrderRepository(IAmazonDynamoDB dynamo, DynamoOptions options) : IOrderRepository
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
            when (cancelled.CancellationReasons?.Exists(
                reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) == true)
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

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
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
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Order '{order.Id}' changed since it was read.");
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

    /// <summary>
    /// Delivered orders whose auto-release deadline has passed.
    /// </summary>
    /// <remarks>
    /// Nothing calls this yet — the release sweep itself is out of scope here, the same
    /// documented gap as the auction close sweep (see <c>ListDueForCloseAsync</c>'s own
    /// remarks). Implemented as a table scan with a filter rather than a purpose-built
    /// sparse index, which is the wrong trade for a caller that runs on every request but
    /// the right one for a method nothing invokes: building <c>gsi_auction_end</c>'s
    /// sharded-index machinery for a sweep that does not exist yet would be speculative.
    /// Revisit alongside actually building the sweep.
    /// </remarks>
    public async Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var due = new List<Order>();
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;

        do
        {
            var response = await dynamo.ScanAsync(
                new ScanRequest
                {
                    TableName = options.TableName,
                    FilterExpression = $"{DynamoTable.EntityType} = :type AND escrowState = :delivered",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":type"] = Attr.S(OrderItem.TypeName),
                        [":delivered"] = Attr.EnumValue(EscrowState.Delivered),
                    },
                    ExclusiveStartKey = exclusiveStartKey,
                },
                ct).ConfigureAwait(false);

            foreach (var item in response.Items ?? [])
            {
                var order = OrderItem.ToDomain(item);
                if (order.AutoReleaseAt is { } autoReleaseAt && autoReleaseAt <= asOf)
                {
                    due.Add(order);
                }
            }

            exclusiveStartKey = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        } while (exclusiveStartKey is not null && due.Count < limit);

        return due.Count > limit ? due.GetRange(0, limit) : due;
    }

    private async Task<Page<Order>> QueryIndexAsync(
        string indexName, string pkAttribute, string pkValue, string skAttribute,
        string? cursor, int limit, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
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
