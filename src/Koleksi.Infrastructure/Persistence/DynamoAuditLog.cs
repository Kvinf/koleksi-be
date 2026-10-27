using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// The audit rows of one entry, ready to be added to the DynamoDB transaction of the change they describe.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is how "no change without an audit row" is enforced.</b> A repository that changes data on a
/// staff member's behalf builds its own <c>TransactWriteItems</c> request and appends
/// <see cref="For"/> to it. DynamoDB applies every item or none, so if the audit rows cannot be written the
/// change is not made, and if the change fails its condition no audit row is left behind.
/// </para>
/// <para>
/// Each Put carries <c>attribute_not_exists(pk)</c>, so an existing audit row can never be replaced through
/// this path. IAM cannot require a condition expression, so that guarantee holds for this code and not for
/// a principal that writes with its own request; what IAM does enforce is that the admin Lambda cannot
/// update or delete an <c>AUDIT#</c> row at all (see the admin stack).
/// </para>
/// <para>
/// A transaction holds at most 100 items; an entry takes three of them.
/// </para>
/// </remarks>
internal static class AuditItems
{
    public const int ItemsPerEntry = 3;

    public const string EntityName = "Audit";

    public static IReadOnlyList<TransactWriteItem> For(AuditEntry entry, string tableName)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var sk = DynamoTable.AuditSk(entry.At, entry.Id);
        return
        [
            Put(tableName, DynamoTable.AuditMonthPk(entry.At), sk, entry),
            Put(tableName, DynamoTable.AuditActorPk(entry.ActorId), sk, entry),
            Put(tableName, DynamoTable.AuditTargetPk(entry.TargetType, entry.TargetId), sk, entry),
        ];
    }

    private static TransactWriteItem Put(string tableName, string pk, string sk, AuditEntry entry)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(pk),
            [DynamoTable.Sk] = Attr.S(sk),
            [DynamoTable.EntityType] = Attr.S(EntityName),
            ["id"] = Attr.S(entry.Id),
            ["at"] = Attr.Instant(entry.At),
            ["actorId"] = Attr.S(entry.ActorId),
            ["actorName"] = Attr.S(entry.ActorName),
            ["action"] = Attr.S(entry.Action),
            ["targetType"] = Attr.S(entry.TargetType),
            ["targetId"] = Attr.S(entry.TargetId),
            ["requestId"] = Attr.S(entry.RequestId),
        };

        // Absent text is left out rather than stored as NULL, so a reader cannot mistake "no reason" for a reason.
        AddIfPresent(item, "reason", entry.Reason);
        AddIfPresent(item, "before", entry.Before);
        AddIfPresent(item, "after", entry.After);

        return new TransactWriteItem
        {
            Put = new Put
            {
                TableName = tableName,
                Item = item,
                ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
            },
        };
    }

    private static void AddIfPresent(Dictionary<string, AttributeValue> item, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            item[name] = Attr.S(value);
        }
    }

    public static AuditEntry ToEntry(Dictionary<string, AttributeValue> item) =>
        AuditEntry.Restore(
            item.GetString("id"),
            item.GetInstant("at"),
            item.GetString("actorId"),
            item.GetString("actorName"),
            item.GetString("action"),
            item.GetString("targetType"),
            item.GetString("targetId"),
            item.GetString("requestId"),
            item.GetStringOrNull("reason"),
            item.GetStringOrNull("before"),
            item.GetStringOrNull("after"));
}

/// <summary>The append-only audit log, on the single table. See <see cref="AuditItems"/>.</summary>
internal sealed class DynamoAuditLog(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock) : IAuditLog
{
    /// <summary>How many calendar months back the unfiltered view will look before it gives up.</summary>
    internal const int MaxMonthsBack = 24;

    public async Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await dynamo.TransactWriteItemsAsync(
            new TransactWriteItemsRequest
            {
                TransactItems = [.. AuditItems.For(entry, options.TableName)],

                // A retry of this exact call (the SDK retries a transaction whose answer it never saw) is
                // recognised by DynamoDB for ten minutes and does not write a second set of rows.
                ClientRequestToken = entry.Id,
            },
            ct).ConfigureAwait(false);
    }

    public async Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ActorId is not null)
        {
            return await ListPartitionAsync(DynamoTable.AuditActorPk(query.ActorId), query, ct).ConfigureAwait(false);
        }

        if (query.TargetId is not null && query.TargetType is not null)
        {
            return await ListPartitionAsync(DynamoTable.AuditTargetPk(query.TargetType, query.TargetId), query, ct)
                .ConfigureAwait(false);
        }

        return await ListMonthsAsync(query, ct).ConfigureAwait(false);
    }

    /// <summary>One partition, newest first. The cursor is the sort key of the last entry returned.</summary>
    private async Task<AuditPage> ListPartitionAsync(string pk, AuditQuery query, CancellationToken ct)
    {
        var sk = query.Cursor is null ? null : AuditCursor.ParseSortKey(query.Cursor);
        var items = await QueryAsync(pk, query.Limit, sk, ct).ConfigureAwait(false);

        var next = items.Count == query.Limit && items.Count > 0
            ? AuditCursor.ForSortKey(DynamoTable.AuditSk(items[^1].At, items[^1].Id))
            : null;
        return new AuditPage(items, next);
    }

    /// <summary>
    /// Every entry, newest first: the current month's partition, then the month before when it runs out.
    /// The cursor names the month and the last sort key, so a page boundary can fall anywhere.
    /// </summary>
    private async Task<AuditPage> ListMonthsAsync(AuditQuery query, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        string? afterSk = null;

        if (query.Cursor is not null)
        {
            (month, afterSk) = AuditCursor.ParseMonthCursor(query.Cursor);
        }

        var oldest = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-MaxMonthsBack);
        var collected = new List<AuditEntry>();

        while (collected.Count < query.Limit && month >= oldest)
        {
            var pk = DynamoTable.AuditMonthPk(month.ToString("yyyyMM", CultureInfo.InvariantCulture));
            var page = await QueryAsync(pk, query.Limit - collected.Count, afterSk, ct).ConfigureAwait(false);
            collected.AddRange(page);

            // A short page means this month is used up; carry on with the one before, from its newest entry.
            month = month.AddMonths(-1);
            afterSk = null;
        }

        var next = collected.Count == query.Limit && collected.Count > 0
            ? AuditCursor.ForMonth(collected[^1].At, DynamoTable.AuditSk(collected[^1].At, collected[^1].Id))
            : null;
        return new AuditPage(collected, next);
    }

    private async Task<List<AuditEntry>> QueryAsync(string pk, int limit, string? afterSk, CancellationToken ct)
    {
        var request = new QueryRequest
        {
            TableName = options.TableName,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#pk"] = DynamoTable.Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal) { [":pk"] = Attr.S(pk) },
            ScanIndexForward = false,
            Limit = limit,

            // A staff member who just did something expects to see it in the log on the next page load.
            ConsistentRead = true,
        };

        if (afterSk is not null)
        {
            request.ExclusiveStartKey = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = Attr.S(pk),
                [DynamoTable.Sk] = Attr.S(afterSk),
            };
        }

        var response = await dynamo.QueryAsync(request, ct).ConfigureAwait(false);
        return [.. response.Items.Select(AuditItems.ToEntry)];
    }
}

/// <summary>
/// The opaque page cursors of the audit view. They are parsed strictly: a cursor is caller input, and one that does
/// not match the exact shape is refused rather than turned into a key.
/// </summary>
internal static partial class AuditCursor
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z#[0-9A-Za-z]{26}$", RegexOptions.CultureInvariant)]
    private static partial Regex SortKeyShape();

    [GeneratedRegex(@"^\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthShape();

    public static string ForSortKey(string sortKey) => Encode(sortKey);

    public static string ForMonth(DateTimeOffset at, string sortKey) =>
        Encode($"{at.UtcDateTime.ToString("yyyyMM", CultureInfo.InvariantCulture)}|{sortKey}");

    public static string ParseSortKey(string cursor)
    {
        var text = Decode(cursor);
        return SortKeyShape().IsMatch(text) ? text : throw Invalid();
    }

    public static (DateTime Month, string SortKey) ParseMonthCursor(string cursor)
    {
        var parts = Decode(cursor).Split('|');
        if (parts.Length != 2
            || !MonthShape().IsMatch(parts[0])
            || !SortKeyShape().IsMatch(parts[1])
            || !DateTime.TryParseExact(
                parts[0] + "01", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
        {
            throw Invalid();
        }

        return (month, parts[1]);
    }

    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            throw Invalid();
        }
    }

    private static DomainException Invalid() => DomainException.For("admin.audit_query_invalid", "The page cursor is not valid.");
}
