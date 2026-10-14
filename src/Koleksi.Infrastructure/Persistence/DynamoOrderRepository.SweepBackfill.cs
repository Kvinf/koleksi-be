using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>The sweep-key backfill (QA D-12). See <see cref="IOrderSweepBackfill"/>.</summary>
internal sealed partial class DynamoOrderRepository
{
    public async Task<OrderSweepBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // A scan, deliberately: the rows this exists for are exactly the ones no index can reach.
        // Limit counts rows READ, not rows returned, so one call costs a bounded number of reads
        // however large the table is. The filter keeps only orders that have a clock running and
        // no sweep entry yet.
        var response = await dynamo.ScanAsync(
            new ScanRequest
            {
                TableName = options.TableName,
                Limit = limit,
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                FilterExpression =
                    "#entity = :order AND #state IN (:awaiting, :delivered) AND attribute_not_exists(#duePk)",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#entity"] = DynamoTable.EntityType,
                    ["#state"] = OrderItem.EscrowStateAttribute,
                    ["#duePk"] = DynamoTable.AuctionEndPk,
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":order"] = Attr.S(OrderItem.TypeName),
                    [":awaiting"] = Attr.S(nameof(Koleksi.Domain.Orders.EscrowState.AwaitingPayment)),
                    [":delivered"] = Attr.S(nameof(Koleksi.Domain.Orders.EscrowState.Delivered)),
                },
            },
            ct).ConfigureAwait(false);

        var updated = 0;
        foreach (var item in response.Items ?? [])
        {
            var order = OrderItem.ToDomain(item);
            if (OrderItem.DueEntryFor(order) is not { } due)
            {
                continue;
            }

            try
            {
                // UpdateItem, not a whole-item put: only the two sweep attributes are written, and
                // only while the row is still at the version we read and still has none. A row that
                // changed meanwhile was rewritten whole by its own update, which carries the keys.
                await dynamo.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = options.TableName,
                        Key = KeyOf(order.Id),
                        UpdateExpression = "SET #duePk = :pk, #dueSk = :sk",
                        ConditionExpression = "#v = :version AND attribute_not_exists(#duePk)",
                        ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["#duePk"] = DynamoTable.AuctionEndPk,
                            ["#dueSk"] = DynamoTable.AuctionEndSk,
                            ["#v"] = OrderItem.VersionAttribute,
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [":pk"] = Attr.S(DynamoTable.OrderDuePkValue(due.State, DynamoTable.ShardOf(order.Id))),
                            [":sk"] = Attr.S(DynamoTable.OrderDueSkValue(due.DueAt, order.Id)),
                            [":version"] = Attr.N(order.Version),
                        },
                    },
                    ct).ConfigureAwait(false);
                updated++;
            }
            catch (Amazon.DynamoDBv2.Model.ConditionalCheckFailedException)
            {
                // Changed or already keyed since the scan: nothing to do.
            }
        }

        return new OrderSweepBackfillPage(response.ScannedCount ?? 0, updated, DynamoCursor.Encode(response.LastEvaluatedKey));
    }
}
