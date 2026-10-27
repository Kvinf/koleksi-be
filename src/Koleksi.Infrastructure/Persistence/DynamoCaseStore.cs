using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// What the public site reads of the rows the back office writes for a dispute. See <see cref="ICaseStore"/>.
/// </summary>
/// <remarks>
/// Every read is a query on one partition or a batch get on known keys: no <c>Scan</c>. The staff-internal notes live in the same
/// partition as the shared ones, so they are excluded twice: by the query's filter, and again when each item is read back. The
/// rows are the ones <see cref="DynamoDisputeAdminStore"/> writes; this class never writes a staff note, a refund or a notice. It
/// flips a notice from pending to delivered, and records a buyer's or seller's reply.
/// </remarks>
internal sealed class DynamoCaseStore(IAmazonDynamoDB dynamo, DynamoOptions options) : ICaseStore
{
    private const int BatchGetLimit = 100;

    /// <summary>Pages of a person's notices read at most. A notice is written for each side of each ruling and each request, so this is far beyond real use.</summary>
    private const int NoticePageCeiling = 5;

    private const string Pending = "pending";
    private const string Delivered = "delivered";

    public async Task<IReadOnlyList<DisputeNote>> ListSharedNotesAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var notes = new List<DisputeNote>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :prefix)",

                    // The internal kind is filtered out before it leaves DynamoDB.
                    FilterExpression = "#kind <> :internal",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#pk"] = DynamoTable.Pk,
                        ["#sk"] = DynamoTable.Sk,
                        ["#kind"] = "kind",
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.OrderPk(orderId)),
                        [":prefix"] = Attr.S(DynamoTable.DisputeNoteSkPrefix()),
                        [":internal"] = Attr.EnumValue(DisputeNoteKind.Note),
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            notes.AddRange((response.Items ?? []).Select(DisputeItems.NoteToDomain).Where(note => note.Kind != DisputeNoteKind.Note));
            start = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        }
        while (start is not null);

        return notes;
    }

    public async Task<IReadOnlyList<OrderNotice>> ListPendingNoticesAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var notices = new List<OrderNotice>();
        Dictionary<string, AttributeValue>? start = null;
        for (var page = 0; page < NoticePageCeiling; page++)
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = "#pk = :pk",
                    FilterExpression = "#status = :pending",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#pk"] = DynamoTable.Pk,
                        ["#status"] = "status",
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        // The partition is the person's own: the id comes from their token, never from the request.
                        [":pk"] = Attr.S(DynamoTable.NoticePk(userId)),
                        [":pending"] = Attr.S(Pending),
                    },
                    ScanIndexForward = false,
                    ConsistentRead = true,
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            notices.AddRange((response.Items ?? []).Select(ToNotice));
            start = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
            if (start is null)
            {
                break;
            }
        }

        return notices;
    }

    public async Task<IReadOnlyList<OrderNotice>> MarkNoticesDeliveredAsync(
        string userId, IReadOnlyCollection<OrderNotice> notices, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(notices);

        var delivered = new List<OrderNotice>();
        foreach (var notice in notices)
        {
            // Never another person's: the key is built from the caller's id, so a notice that belongs to someone else would be
            // addressed to a row that does not exist and the condition below would refuse it.
            if (!string.Equals(notice.UserId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await dynamo.UpdateItemAsync(
                    new UpdateItemRequest
                    {
                        TableName = options.TableName,
                        Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [DynamoTable.Pk] = Attr.S(DynamoTable.NoticePk(userId)),
                            [DynamoTable.Sk] = Attr.S(DynamoTable.NoticeSk(notice.CreatedAt, notice.Id)),
                        },
                        UpdateExpression = "SET #status = :delivered, #at = :at",
                        ConditionExpression = "#status = :pending",
                        ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["#status"] = "status",
                            ["#at"] = "deliveredAt",
                        },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [":delivered"] = Attr.S(Delivered),
                            [":pending"] = Attr.S(Pending),
                            [":at"] = Attr.Instant(at),
                        },
                    },
                    ct).ConfigureAwait(false);

                delivered.Add(notice);
            }
            catch (ConditionalCheckFailedException)
            {
                // Already delivered by a read that ran at the same time: that read tells the person, not this one.
            }
        }

        return delivered;
    }

    public async Task AddReplyAsync(CaseReplyCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var orderPk = Attr.S(DynamoTable.OrderPk(commit.Order.Id));
        var items = new List<TransactWriteItem>
        {
            // The order is only checked, never rewritten: a reply and a ruling racing are ordered by this condition, and a reply to a
            // case that was just ruled on is refused.
            new()
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = orderPk,
                        [DynamoTable.Sk] = Attr.S(DynamoTable.OrderMetaSk()),
                    },
                    ConditionExpression = "#state = :disputed",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#state"] = OrderItem.EscrowStateAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":disputed"] = Attr.S(nameof(EscrowState.Disputed)),
                    },
                },
            },
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = DisputeItems.NoteToItem(commit.Reply),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
        };

        if (commit.ClearAwaitingFor is { } askedAt)
        {
            // Only the marker of the request that was answered. If staff asked again meanwhile it carries a newer time, the
            // condition fails and the whole reply is refused, so the new question is never wiped. An absent marker is fine.
            items.Add(new TransactWriteItem
            {
                Delete = new Delete
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = orderPk,
                        [DynamoTable.Sk] = Attr.S(DynamoTable.DisputeInfoSk()),
                    },
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk}) OR #at = :asked",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#at"] = "at" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":asked"] = Attr.Instant(askedAt),
                    },
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Order '{commit.Order.Id}' is no longer in the state this reply needed.");
        }
    }

    public async Task<IReadOnlyDictionary<string, Refund>> FindRefundsAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        var found = new Dictionary<string, Refund>(StringComparer.Ordinal);
        foreach (var chunk in orderIds.Distinct(StringComparer.Ordinal).Chunk(BatchGetLimit))
        {
            var request = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [options.TableName] = new KeysAndAttributes
                {
                    Keys =
                    [
                        .. chunk.Select(id => new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [DynamoTable.Pk] = Attr.S(DynamoTable.RefundPk(id)),
                            [DynamoTable.Sk] = Attr.S(DynamoTable.RefundSk()),
                        }),
                    ],
                    ConsistentRead = true,
                },
            };

            // BatchGetItem may answer part of a request; the rest comes back as UnprocessedKeys. Bounded, so it cannot spin.
            for (var attempt = 0; attempt < 5 && request.Count > 0; attempt++)
            {
                var response = await dynamo.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = request }, ct).ConfigureAwait(false);

                foreach (var item in response.Responses?.GetValueOrDefault(options.TableName) ?? [])
                {
                    var refund = DisputeItems.RefundToDomain(item);
                    found[refund.OrderId] = refund;
                }

                request = response.UnprocessedKeys is { Count: > 0 } rest ? rest : [];
            }
        }

        return found;
    }

    private static OrderNotice ToNotice(Dictionary<string, AttributeValue> item) =>
        new(
            item.GetString("id"),
            item.GetString("userId"),
            item.GetEnum<OrderNoticeKind>("kind"),
            item.GetString("orderId"),
            item.GetString("orderNumber"),
            item.GetString("text"),
            item.GetInstant("createdAt"));
}
