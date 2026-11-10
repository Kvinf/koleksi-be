using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Bounties;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Reports on Dicari and the staff changes that settle them. See <see cref="IBountyModerationStore"/>.
/// </summary>
/// <remarks>
/// A report is a row in its Dicari's partition (<c>REPORT#{reporterId}</c>), written in one transaction with an atomic
/// advance of <c>reportCount</c> on the Dicari, which is what enforces both "one report per person" (the row's own
/// condition) and "at most <see cref="BountyReport.MaxPerBounty"/>" (the count's condition). The counter is not part of the
/// Dicari's mapped item, so ordinary Dicari writes never touch it.
/// </remarks>
internal sealed class DynamoBountyModerationStore(IAmazonDynamoDB dynamo, DynamoOptions options) : IBountyModerationStore
{
    private const string ReportCountAttr = "reportCount";

    public async Task<bool> AddReportAsync(BountyReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = MetaKey(report.BountyId),
                    UpdateExpression = "ADD #rc :one",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND (attribute_not_exists(#rc) OR #rc < :max)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#rc"] = ReportCountAttr },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":one"] = Attr.N(1),
                        [":max"] = Attr.N(BountyReport.MaxPerBounty),
                    },
                },
            },
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = BountyReportItem.ToItem(report),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
        };

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
            return true;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<BountyReport>> ListReportsAsync(string bountyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);

        var reports = new List<BountyReport>();
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
                        [":pk"] = Attr.S(DynamoTable.BountyPk(bountyId)),
                        [":prefix"] = Attr.S(DynamoTable.BountyReportSkPrefixValue()),
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            reports.AddRange((response.Items ?? []).Select(BountyReportItem.ToDomain));
            start = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        }
        while (start is not null);

        return [.. reports.OrderBy(r => r.CreatedAt)];
    }

    public async Task<IReadOnlyList<BountyReport>> ListQueueAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.AuctionEndIndex,
                KeyConditionExpression = "#pk = :pk",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#pk"] = DynamoTable.AuctionEndPk },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.BountyReportQueuePkValue()),
                },
                ScanIndexForward = false,
                Limit = Math.Min(limit, 200),
            },
            ct).ConfigureAwait(false);

        return [.. (response.Items ?? []).Select(BountyReportItem.ToDomain)];
    }

    public async Task RemoveAsync(
        Bounty bounty, long expectedVersion, IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        ArgumentNullException.ThrowIfNull(settled);
        ArgumentNullException.ThrowIfNull(audit);

        var writes = new List<TransactWriteItem>
        {
            new() { Update = DynamoBountyRepository.BuildUpdate(options.TableName, bounty, expectedVersion) },
        };
        writes.AddRange(settled.Select(DeleteReport));
        writes.AddRange(AuditItems.For(audit, options.TableName));

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Dicari '{bounty.Id}' changed since it was read.");
        }
    }

    public async Task DismissAsync(IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settled);
        ArgumentNullException.ThrowIfNull(audit);

        if (settled.Count == 0)
        {
            return;
        }

        var bountyId = settled[0].BountyId;
        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = MetaKey(bountyId),
                    UpdateExpression = "REMOVE #rc",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#rc"] = ReportCountAttr },
                },
            },
        };
        writes.AddRange(settled.Select(DeleteReport));
        writes.AddRange(AuditItems.For(audit, options.TableName));

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Dicari '{bountyId}' reports changed since they were read.");
        }
    }

    private TransactWriteItem DeleteReport(BountyReport report) =>
        new()
        {
            Delete = new Delete
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(report.BountyId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.BountyReportSk(report.ReporterId)),
                },
            },
        };

    private static Dictionary<string, AttributeValue> MetaKey(string bountyId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bountyId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyMetaSk()),
        };
}

internal static class BountyReportItem
{
    public const string TypeName = "BountyReport";
    private const string BountyIdAttr = "bountyId";
    private const string ReporterIdAttr = "reporterId";
    private const string ReasonAttr = "reason";
    private const string NoteAttr = "note";
    private const string CreatedAtAttr = "createdAt";

    public static Dictionary<string, AttributeValue> ToItem(BountyReport report) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(report.BountyId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyReportSk(report.ReporterId)),
            [DynamoTable.EntityType] = Attr.S(TypeName),
            [BountyIdAttr] = Attr.S(report.BountyId),
            [ReporterIdAttr] = Attr.S(report.ReporterId),
            [ReasonAttr] = Attr.EnumValue(report.Reason),
            [NoteAttr] = Attr.SOrNull(report.Note),
            [CreatedAtAttr] = Attr.Instant(report.CreatedAt),

            // The staff queue. Present only while the report is unsettled; deleting the row removes it.
            [DynamoTable.AuctionEndPk] = Attr.S(DynamoTable.BountyReportQueuePkValue()),
            [DynamoTable.AuctionEndSk] = Attr.S(DynamoTable.BountyReportQueueSkValue(report.CreatedAt, report.BountyId, report.ReporterId)),
        };

    public static BountyReport ToDomain(Dictionary<string, AttributeValue> item) =>
        BountyReport.Rehydrate(
            item.GetString(BountyIdAttr),
            item.GetString(ReporterIdAttr),
            item.GetEnum<BountyReportReason>(ReasonAttr),
            item.GetStringOrNull(NoteAttr),
            item.GetInstant(CreatedAtAttr));
}
