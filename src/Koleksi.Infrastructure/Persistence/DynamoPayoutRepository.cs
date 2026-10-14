using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="Payout"/> to and from its stored item.</summary>
internal static class PayoutItem
{
    public const string TypeName = "Payout";

    /// <summary>The due-index state a pending payout sits under until its next attempt time.</summary>
    public const string DueState = "Payout";

    private const string OrderIdAttr = "orderId";
    private const string OrderNumberAttr = "orderNumber";
    private const string SellerIdAttr = "sellerId";
    private const string AmountAttr = "amount";
    private const string FeeAttr = "fee";
    private const string StatusAttr = "status";
    private const string CreatedAtAttr = "createdAt";
    private const string NextAttemptAtAttr = "nextAttemptAt";
    private const string PaidAtAttr = "paidAt";
    private const string ReferenceAttr = "providerReference";
    private const string AttemptsAttr = "attempts";
    private const string LastErrorAttr = "lastError";
    private const string VersionAttr = "version";

    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> KeyOf(string orderId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.PayoutPk(orderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.PayoutMetaSk()),
        };

    public static Dictionary<string, AttributeValue> ToItem(Payout payout)
    {
        var createdAt = DynamoTable.FormatInstant(payout.CreatedAt);

        var item = KeyOf(payout.OrderId);
        item[DynamoTable.EntityType] = Attr.S(TypeName);
        item[OrderIdAttr] = Attr.S(payout.OrderId);
        item[OrderNumberAttr] = Attr.S(payout.OrderNumber);
        item[SellerIdAttr] = Attr.S(payout.SellerId);
        item[AmountAttr] = Attr.MoneyValue(payout.Amount);
        item[FeeAttr] = Attr.MoneyValue(payout.Fee);
        item[StatusAttr] = Attr.EnumValue(payout.Status);
        item[CreatedAtAttr] = Attr.S(createdAt);
        item[NextAttemptAtAttr] = Attr.Instant(payout.NextAttemptAt);
        item[PaidAtAttr] = Attr.InstantOrNull(payout.PaidAt);
        item[ReferenceAttr] = Attr.SOrNull(payout.ProviderReference);
        item[AttemptsAttr] = Attr.N(payout.Attempts);
        item[LastErrorAttr] = Attr.SOrNull(payout.LastError);
        item[VersionAttr] = Attr.N(payout.Version);

        // The seller's own list of payouts, newest first.
        item[DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(payout.SellerId));
        item[DynamoTable.SellerSk] = Attr.S(DynamoTable.SellerSkValue(DynamoTable.PayoutIndexPrefix(), createdAt, payout.OrderId));

        // The sweep's index entry: only while pending, due at the next attempt. A paid or failed payout is
        // written without it, which removes it from the sweep.
        if (payout.Status == PayoutStatus.Pending)
        {
            item[DynamoTable.AuctionEndPk] = Attr.S(DynamoTable.AuctionEndPkValue(DueState, DynamoTable.ShardOf(payout.OrderId)));
            item[DynamoTable.AuctionEndSk] = Attr.S(DynamoTable.AuctionEndSkValue(payout.NextAttemptAt, payout.OrderId));
        }

        return item;
    }

    public static Payout ToDomain(Dictionary<string, AttributeValue> item) => Payout.Rehydrate(
        item.GetString(OrderIdAttr),
        item.GetString(OrderNumberAttr),
        item.GetString(SellerIdAttr),
        item.GetMoney(AmountAttr),
        item.GetMoney(FeeAttr),
        item.GetEnum<PayoutStatus>(StatusAttr),
        item.GetInstant(CreatedAtAttr),
        item.GetInstant(NextAttemptAtAttr),
        item.GetInstantOrNull(PaidAtAttr),
        item.GetStringOrNull(ReferenceAttr),
        item.GetInt(AttemptsAttr),
        item.GetStringOrNull(LastErrorAttr),
        item.GetLongOrDefault(VersionAttr));
}

/// <summary>Stores payouts, one item per order, with a sparse index of the ones waiting to be sent.</summary>
internal sealed class DynamoPayoutRepository(IAmazonDynamoDB dynamo, DynamoOptions options) : IPayoutRepository
{
    public async Task<bool> AddIfAbsentAsync(Payout payout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payout);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = PayoutItem.ToItem(payout),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<Payout?> FindByOrderAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = PayoutItem.KeyOf(orderId), ConsistentRead = true },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? PayoutItem.ToDomain(item) : null;
    }

    public async Task UpdateAsync(Payout payout, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payout);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = PayoutItem.ToItem(payout),
                    ConditionExpression = "#v = :expected",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = PayoutItem.VersionAttribute,
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
            throw new ConcurrencyConflictException($"Payout for order '{payout.OrderId}' changed since it was read.");
        }
    }

    public async Task<IReadOnlyList<Payout>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Fan out over the shards and merge, as the auction and order sweeps do.
        var perShard = Math.Max(1, limit / DynamoTable.AuctionShardCount);
        var queries = new Task<QueryResponse>[DynamoTable.AuctionShardCount];

        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            queries[shard] = dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.AuctionEndIndex,
                    KeyConditionExpression = $"{DynamoTable.AuctionEndPk} = :pk AND {DynamoTable.AuctionEndSk} <= :now",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.AuctionEndPkValue(PayoutItem.DueState, shard)),
                        [":now"] = Attr.S(DynamoTable.EndsAtUpperBound(asOf)),
                    },
                    Limit = perShard,
                    ScanIndexForward = true,
                },
                ct);
        }

        var responses = await Task.WhenAll(queries).ConfigureAwait(false);

        var due = new List<Payout>();
        foreach (var response in responses)
        {
            foreach (var item in response.Items ?? [])
            {
                due.Add(PayoutItem.ToDomain(item));
            }
        }

        due.Sort((left, right) => left.NextAttemptAt.CompareTo(right.NextAttemptAt));
        return due.Count > limit ? due.GetRange(0, limit) : due;
    }

    public async Task<Page<Payout>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.SellerIndex,
                KeyConditionExpression = $"{DynamoTable.SellerPk} = :pk AND begins_with({DynamoTable.SellerSk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.SellerPkValue(sellerId)),
                    [":prefix"] = Attr.S(DynamoTable.PayoutIndexPrefix()),
                },
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                Limit = limit,
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var items = (response.Items ?? []).Select(PayoutItem.ToDomain).ToList();
        return new Page<Payout>(items, DynamoCursor.Encode(response.LastEvaluatedKey), items.Count);
    }
}
