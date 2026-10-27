using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>How a case note, an information-request marker, a notice and a refund are stored.</summary>
internal static class DisputeItems
{
    public const string NoteEntity = "DisputeNote";
    public const string InfoEntity = "DisputeInfo";
    public const string NoticeEntity = "OrderNotice";
    public const string RefundEntity = "Refund";

    public static Dictionary<string, AttributeValue> NoteToItem(DisputeNote note)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(note.OrderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.DisputeNoteSk(note.Id)),
            [DynamoTable.EntityType] = Attr.S(NoteEntity),
            ["id"] = Attr.S(note.Id),
            ["orderId"] = Attr.S(note.OrderId),
            ["at"] = Attr.Instant(note.At),
            ["staffId"] = Attr.S(note.StaffId),
            ["staffName"] = Attr.S(note.StaffName),
            ["kind"] = Attr.EnumValue(note.Kind),
            ["text"] = Attr.S(note.Text),
        };

        if (note.To is { } to)
        {
            item["to"] = Attr.EnumValue(to);
        }

        return item;
    }

    public static DisputeNote NoteToDomain(Dictionary<string, AttributeValue> item) =>
        new(
            item.GetString("id"),
            item.GetString("orderId"),
            item.GetInstant("at"),
            item.GetString("staffId"),
            item.GetString("staffName"),
            item.GetEnum<DisputeNoteKind>("kind"),
            item.GetStringOrNull("to") is { } to ? Enum.Parse<DisputeParty>(to) : null,
            item.GetString("text"));

    public static Dictionary<string, AttributeValue> InfoMarker(DisputeNote note) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(note.OrderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.DisputeInfoSk()),
            [DynamoTable.EntityType] = Attr.S(InfoEntity),
            ["at"] = Attr.Instant(note.At),
            ["to"] = Attr.EnumValue(note.To ?? DisputeParty.Buyer),
        };

    public static Dictionary<string, AttributeValue> NoticeToItem(OrderNotice notice) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.NoticePk(notice.UserId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.NoticeSk(notice.CreatedAt, notice.Id)),
            [DynamoTable.EntityType] = Attr.S(NoticeEntity),
            ["id"] = Attr.S(notice.Id),
            ["userId"] = Attr.S(notice.UserId),
            ["kind"] = Attr.EnumValue(notice.Kind),
            ["orderId"] = Attr.S(notice.OrderId),
            ["orderNumber"] = Attr.S(notice.OrderNumber),
            ["text"] = Attr.S(notice.Text),
            ["createdAt"] = Attr.Instant(notice.CreatedAt),

            // Nothing delivers a notice yet. This says so on every row, so a future sender can pick up exactly these.
            ["status"] = Attr.S("pending"),
        };

    public static Dictionary<string, AttributeValue> RefundToItem(Refund refund)
    {
        var item = RefundCore(refund);
        item["remainder"] = Attr.MoneyValue(refund.Remainder);
        item["disposition"] = Attr.EnumValue(refund.Disposition);
        item["sellerPayout"] = Attr.MoneyValue(refund.SellerPayout);
        if (refund.SettledAt is { } settledAt)
        {
            item["settledAt"] = Attr.Instant(settledAt);
        }

        return item;
    }

    private static Dictionary<string, AttributeValue> RefundCore(Refund refund) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.RefundPk(refund.OrderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.RefundSk()),
            [DynamoTable.EntityType] = Attr.S(RefundEntity),
            ["id"] = Attr.S(refund.Id),
            ["orderId"] = Attr.S(refund.OrderId),
            ["orderNumber"] = Attr.S(refund.OrderNumber),
            ["buyerId"] = Attr.S(refund.BuyerId),
            ["amount"] = Attr.MoneyValue(refund.Amount),
            ["reason"] = Attr.S(refund.Reason),
            ["staffId"] = Attr.S(refund.StaffId),
            ["staffName"] = Attr.S(refund.StaffName),
            ["createdAt"] = Attr.Instant(refund.CreatedAt),
            ["status"] = Attr.EnumValue(refund.Status),
            ["statusAt"] = Attr.Instant(refund.StatusAt),
        };

    public static Refund RefundToDomain(Dictionary<string, AttributeValue> item) =>
        Refund.Rehydrate(
            item.GetString("id"),
            item.GetString("orderId"),
            item.GetString("orderNumber"),
            item.GetString("buyerId"),
            item.GetMoney("amount"),
            item.GetString("reason"),
            item.GetString("staffId"),
            item.GetString("staffName"),
            item.GetInstant("createdAt"),
            item.GetEnum<RefundStatus>("status"),
            item.GetInstant("statusAt"),

            // A refund written before remainders existed has none of these and reads as having no remainder.
            item.GetMoneyOrNull("remainder"),
            item.GetStringOrNull("disposition") is { } disposition ? Enum.Parse<RemainderDisposition>(disposition) : RemainderDisposition.None,
            item.GetMoneyOrNull("sellerPayout"),
            item.GetInstantOrNull("settledAt"));

    /// <summary>Moves a held remainder to its settlement, only if it is still held.</summary>
    public static (string Update, Dictionary<string, string> Names, Dictionary<string, AttributeValue> Values) SettleRefund(Refund refund) =>
        (
            "SET #disposition = :disposition, #sellerPayout = :sellerPayout, #settledAt = :settledAt",
            new(StringComparer.Ordinal) { ["#disposition"] = "disposition", ["#sellerPayout"] = "sellerPayout", ["#settledAt"] = "settledAt" },
            new(StringComparer.Ordinal)
            {
                [":disposition"] = Attr.EnumValue(refund.Disposition),
                [":sellerPayout"] = Attr.MoneyValue(refund.SellerPayout),
                [":settledAt"] = Attr.Instant(refund.SettledAt!.Value),
            });
}

/// <summary>
/// The back office's dispute queues and the writes behind a staff ruling. See <see cref="IDisputeAdminStore"/>.
/// </summary>
/// <remarks>
/// Every write is a single <c>TransactWriteItems</c> that carries its audit rows (<see cref="AuditItems.For"/>), so the change and its
/// audit entry exist together or not at all. A ruling is conditional on the order's version and on it still being <c>Disputed</c>, which
/// is what makes two staff ruling at once give one winner. Reads are queries on the sparse sweep index or on one partition:
/// no <c>Scan</c> anywhere on this class.
/// </remarks>
internal sealed class DynamoDisputeAdminStore(IAmazonDynamoDB dynamo, DynamoOptions options) : IDisputeAdminStore
{
    private const int BatchGetLimit = 100;

    public Task<IReadOnlyList<Order>> ListOpenAsync(int limit, CancellationToken ct) =>
        ListQueueAsync(DynamoTable.DisputedQueueState, limit, newestFirst: false, ct);

    public Task<IReadOnlyList<Order>> ListResolvedAsync(int limit, CancellationToken ct) =>
        ListQueueAsync(DynamoTable.DisputeResolvedQueueState, limit, newestFirst: true, ct);

    /// <summary>Fans out over the index's shards and merges, the way the sweeps do, then keeps the first <paramref name="limit"/>.</summary>
    private async Task<IReadOnlyList<Order>> ListQueueAsync(string state, int limit, bool newestFirst, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var queries = new Task<QueryResponse>[DynamoTable.AuctionShardCount];
        for (var shard = 0; shard < queries.Length; shard++)
        {
            queries[shard] = dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.AuctionEndIndex,
                    KeyConditionExpression = $"{DynamoTable.AuctionEndPk} = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.OrderDuePkValue(state, shard)),
                    },

                    // Any shard may hold all of the first `limit`, so each is asked for that many.
                    Limit = limit,
                    ScanIndexForward = !newestFirst,
                },
                ct);
        }

        var responses = await Task.WhenAll(queries).ConfigureAwait(false);

        var found = new List<(DateTimeOffset At, Order Order)>();
        foreach (var response in responses)
        {
            foreach (var item in response.Items ?? [])
            {
                var order = OrderItem.ToDomain(item);
                if (OrderItem.DueEntryFor(order) is { } entry)
                {
                    found.Add((entry.DueAt, order));
                }
            }
        }

        found.Sort((left, right) => newestFirst ? right.At.CompareTo(left.At) : left.At.CompareTo(right.At));
        return [.. found.Take(limit).Select(entry => entry.Order)];
    }

    public async Task<IReadOnlyList<DisputeNote>> ListNotesAsync(string orderId, CancellationToken ct)
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
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#pk"] = DynamoTable.Pk,
                        ["#sk"] = DynamoTable.Sk,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.OrderPk(orderId)),
                        [":prefix"] = Attr.S(DynamoTable.DisputeNoteSkPrefix()),
                    },

                    // A case file is read whole, so it is read right after a note was added.
                    ConsistentRead = true,
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            notes.AddRange((response.Items ?? []).Select(DisputeItems.NoteToDomain));
            start = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        }
        while (start is not null);

        return notes;
    }

    public async Task<IReadOnlySet<string>> AwaitingInfoAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        var waiting = new HashSet<string>(StringComparer.Ordinal);
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
                            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(id)),
                            [DynamoTable.Sk] = Attr.S(DynamoTable.DisputeInfoSk()),
                        }),
                    ],
                    ProjectionExpression = "#pk",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#pk"] = DynamoTable.Pk },
                    ConsistentRead = true,
                },
            };

            // BatchGetItem may answer part of a request; the rest comes back as UnprocessedKeys. Bounded, so it cannot spin.
            for (var attempt = 0; attempt < 5 && request.Count > 0; attempt++)
            {
                var response = await dynamo.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = request }, ct).ConfigureAwait(false);

                foreach (var item in response.Responses?.GetValueOrDefault(options.TableName) ?? [])
                {
                    var pk = item.GetString(DynamoTable.Pk);
                    waiting.Add(pk["ORDER#".Length..]);
                }

                request = response.UnprocessedKeys is { Count: > 0 } rest ? rest : [];
            }
        }

        return waiting;
    }

    public async Task<Refund?> FindRefundAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.RefundPk(orderId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.RefundSk()),
                },
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? DisputeItems.RefundToDomain(item) : null;
    }

    public async Task AddNoteAsync(DisputeNoteCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var items = new List<TransactWriteItem>
        {
            // A note changes nothing about the order, so the order is only checked, never rewritten: a note and a ruling racing
            // are ordered by this condition, and a note on a case that was just ruled on is refused.
            new()
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = options.TableName,
                    Key = OrderKey(commit.Order.Id),
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
            PutNew(DisputeItems.NoteToItem(commit.Note)),
        };

        if (commit.MarkAwaitingInfo)
        {
            // An overwrite is fine: asking again just refreshes the marker.
            items.Add(new TransactWriteItem { Put = new Put { TableName = options.TableName, Item = DisputeItems.InfoMarker(commit.Note) } });
        }

        items.AddRange(commit.Notices.Select(notice => PutNew(DisputeItems.NoticeToItem(notice))));
        items.AddRange(AuditItems.For(commit.Audit, options.TableName));

        await WriteAsync(items, commit.Order.Id, ct).ConfigureAwait(false);
    }

    public async Task ResolveAsync(DisputeResolutionCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var items = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = OrderItem.ToItem(commit.Order),

                    // The version says nobody else wrote the order since it was read; the state says it was still Disputed then.
                    ConditionExpression = "#v = :expected AND #state = :disputed",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = OrderItem.VersionAttribute,
                        ["#state"] = OrderItem.EscrowStateAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":expected"] = Attr.N(commit.ExpectedVersion),
                        [":disputed"] = Attr.S(nameof(EscrowState.Disputed)),
                    },
                },
            },
        };

        if (commit.Payout is { } payout)
        {
            // One payout per order, whatever happens twice: the same item and the same condition EscrowRelease uses.
            items.Add(PutNew(PayoutItem.ToItem(payout)));
        }

        if (commit.Refund is { } refund)
        {
            items.Add(PutNew(DisputeItems.RefundToItem(refund)));
        }

        items.Add(PutNew(DisputeItems.NoteToItem(commit.Decision)));

        // The case is closed: nothing is waiting on anyone any more.
        items.Add(new TransactWriteItem
        {
            Delete = new Delete
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(commit.Order.Id)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.DisputeInfoSk()),
                },
            },
        });

        items.AddRange(commit.Notices.Select(notice => PutNew(DisputeItems.NoticeToItem(notice))));
        items.AddRange(AuditItems.For(commit.Audit, options.TableName));

        await WriteAsync(items, commit.Order.Id, ct).ConfigureAwait(false);
    }

    public async Task SettleRemainderAsync(RemainderSettlementCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var (update, names, values) = DisputeItems.SettleRefund(commit.Refund);
        names["#heldState"] = "disposition";
        values[":held"] = Attr.EnumValue(RemainderDisposition.Held);

        var items = new List<TransactWriteItem>
        {
            // The order is only checked: settling a remainder changes the refund and adds a payout, not the order.
            new()
            {
                ConditionCheck = new ConditionCheck
                {
                    TableName = options.TableName,
                    Key = OrderKey(commit.Order.Id),
                    ConditionExpression = "#state = :refunded",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#state"] = OrderItem.EscrowStateAttribute },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":refunded"] = Attr.S(nameof(EscrowState.Refunded)),
                    },
                },
            },
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.RefundPk(commit.Order.Id)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.RefundSk()),
                    },

                    // Only a remainder still held moves, and only once: two staff settling at once give one winner.
                    UpdateExpression = update,
                    ConditionExpression = "#heldState = :held",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                },
            },

            // One payout per order, whatever happens twice: the same item and the same condition a release uses.
            PutNew(PayoutItem.ToItem(commit.Payout)),
        };

        items.AddRange(AuditItems.For(commit.Audit, options.TableName));

        await WriteAsync(items, commit.Order.Id, ct).ConfigureAwait(false);
    }

    public async Task SaveRefundOutcomeAsync(Refund refund, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refund);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.RefundPk(refund.OrderId)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.RefundSk()),
                    },

                    // Only a refund still Recorded moves, and only once: a repeated send cannot rewrite what happened.
                    UpdateExpression = "SET #status = :status, #at = :at",
                    ConditionExpression = "#status = :recorded",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#status"] = "status",
                        ["#at"] = "statusAt",
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":status"] = Attr.EnumValue(refund.Status),
                        [":at"] = Attr.Instant(refund.StatusAt),
                        [":recorded"] = Attr.EnumValue(RefundStatus.Recorded),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // Already moved by an earlier attempt: nothing to do.
        }
    }

    private async Task WriteAsync(List<TransactWriteItem> items, string orderId, CancellationToken ct)
    {
        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Order '{orderId}' is no longer in the state this change needed.");
        }
    }

    private TransactWriteItem PutNew(Dictionary<string, AttributeValue> item) =>
        new()
        {
            Put = new Put
            {
                TableName = options.TableName,
                Item = item,
                ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
            },
        };

    private static Dictionary<string, AttributeValue> OrderKey(string orderId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(orderId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OrderMetaSk()),
        };
}
