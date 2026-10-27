using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// The holiday list the business-day count skips, one partition per year. See <see cref="IHolidayStore"/>.
/// </summary>
/// <remarks>
/// Reading a year is one <c>Query</c> on one partition. Each change is one transaction with its audit rows. Seeding writes a marker
/// row first in the same transaction, so a year is seeded exactly once even if two Lambdas start together, and a holiday staff
/// deliberately removed is never put back by a later start.
/// </remarks>
internal sealed class DynamoHolidayStore(IAmazonDynamoDB dynamo, DynamoOptions options) : IHolidayStore
{
    public async Task<IReadOnlyList<Holiday>> ListAsync(int year, CancellationToken ct)
    {
        var prefix = $"{Holiday.RequireYear(year):D4}-";
        var holidays = new List<Holiday>();
        Dictionary<string, AttributeValue>? start = null;

        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :year)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#pk"] = DynamoTable.Pk,
                        ["#sk"] = DynamoTable.Sk,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.HolidayPk(year)),
                        [":year"] = Attr.S(prefix),
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = start,
                },
                ct).ConfigureAwait(false);

            holidays.AddRange((response.Items ?? []).Select(ToDomain));
            start = response.LastEvaluatedKey is { Count: > 0 } key ? key : null;
        }
        while (start is not null);

        return holidays;
    }

    public async Task<Holiday?> FindAsync(string isoDate, CancellationToken ct)
    {
        if (!Holiday.TryParseDate(isoDate, out var date))
        {
            return null;
        }

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = Key(date.Year, isoDate), ConsistentRead = true },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ToDomain(item) : null;
    }

    public async Task<HolidayWrite> AddAsync(Holiday holiday, AuditEntry audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(holiday);
        ArgumentNullException.ThrowIfNull(audit);

        var items = new List<TransactWriteItem> { PutNew(ToItem(holiday)) };
        items.AddRange(AuditItems.For(audit, options.TableName));

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, ct).ConfigureAwait(false);
            return HolidayWrite.Written;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            return HolidayWrite.AlreadyExists;
        }
    }

    public async Task<HolidayRemoval> RemoveAsync(string isoDate, AuditEntry audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audit);

        if (!Holiday.TryParseDate(isoDate, out var date))
        {
            return HolidayRemoval.NotFound;
        }

        var items = new List<TransactWriteItem>
        {
            new()
            {
                Delete = new Delete
                {
                    TableName = options.TableName,
                    Key = Key(date.Year, isoDate),
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                },
            },
        };
        items.AddRange(AuditItems.For(audit, options.TableName));

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, ct).ConfigureAwait(false);
            return HolidayRemoval.Removed;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            return HolidayRemoval.NotFound;
        }
    }

    public async Task<bool> SeedAsync(int year, IReadOnlyList<Holiday> holidays, AuditEntry audit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(holidays);
        ArgumentNullException.ThrowIfNull(audit);

        if (holidays.Any(h => h.Year != year))
        {
            throw new ArgumentException("Every holiday must be in the year being seeded.", nameof(holidays));
        }

        // One marker, every holiday and the audit rows: at most 1 + 366 + 3 would not fit a transaction, so a year's seed is bounded.
        if (holidays.Count + 1 + AuditItems.ItemsPerEntry > 100)
        {
            throw new ArgumentException("A year's seed must fit one transaction (at most 96 holidays).", nameof(holidays));
        }

        var items = new List<TransactWriteItem>
        {
            PutNew(new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [DynamoTable.Pk] = Attr.S(DynamoTable.HolidayPk(year)),
                [DynamoTable.Sk] = Attr.S(DynamoTable.HolidaySeededSk()),
                [DynamoTable.EntityType] = Attr.S("HolidaySeed"),
                ["at"] = Attr.Instant(audit.At),
            }),
        };
        items.AddRange(holidays.Select(h => PutNew(ToItem(h))));
        items.AddRange(AuditItems.For(audit, options.TableName));

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, ct).ConfigureAwait(false);
            return true;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            return false;
        }
    }

    private TransactWriteItem PutNew(Dictionary<string, AttributeValue> item) =>
        new()
        {
            Put = new Put { TableName = options.TableName, Item = item, ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})" },
        };

    private static Dictionary<string, AttributeValue> Key(int year, string isoDate) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.HolidayPk(year)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.HolidaySk(isoDate)),
        };

    private static Dictionary<string, AttributeValue> ToItem(Holiday holiday)
    {
        var item = Key(holiday.Year, holiday.Date);
        item[DynamoTable.EntityType] = Attr.S("Holiday");
        item["date"] = Attr.S(holiday.Date);
        item["name"] = Attr.S(holiday.Name);
        item["kind"] = Attr.EnumValue(holiday.Kind);
        return item;
    }

    private static Holiday ToDomain(Dictionary<string, AttributeValue> item) =>
        new(item.GetString("date"), item.GetString("name"), item.GetEnum<HolidayKind>("kind"));
}
