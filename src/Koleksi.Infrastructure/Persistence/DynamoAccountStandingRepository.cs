using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Users;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// An account's <see cref="AccountStanding"/> as one small item in the user's own partition, written with a
/// version condition so two violations arriving together are both counted.
/// </summary>
internal sealed class DynamoAccountStandingRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IAccountStandingRepository
{
    private const string StandingSk = "STANDING";
    private const string EntityTypeName = "AccountStanding";
    private const string WarningsAttr = "warnings";
    private const string LastWarningAtAttr = "lastWarningAt";
    private const string BannedAtAttr = "bannedAt";
    private const string RecentAttr = "recent";
    private const string VersionAttr = "version";
    private const string TypeAttr = "type";
    private const string ReferenceAttr = "reference";
    private const string AtAttr = "at";

    public async Task<AccountStanding?> FindAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(userId),

                // A warning just recorded must be seen by the very next read: a stale read would let a
                // third warning be counted as the second.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ToDomain(item) : null;
    }

    public async Task SaveAsync(AccountStanding standing, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(standing);

        var request = new PutItemRequest
        {
            TableName = options.TableName,
            Item = ToItem(standing),
        };

        if (expectedVersion == 0)
        {
            request.ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})";
        }
        else
        {
            request.ConditionExpression = "#v = :expected";
            request.ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["#v"] = VersionAttr };
            request.ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":expected"] = Attr.N(expectedVersion),
            };
        }

        try
        {
            await dynamo.PutItemAsync(request, ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Standing of '{standing.UserId}' changed since it was read.");
        }
    }

    private static Dictionary<string, AttributeValue> KeyOf(string userId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.UserPk(userId)),
            [DynamoTable.Sk] = Attr.S(StandingSk),
        };

    internal static Dictionary<string, AttributeValue> ToItem(AccountStanding standing)
    {
        var recent = new List<AttributeValue>(standing.Recent.Count);
        foreach (var violation in standing.Recent)
        {
            recent.Add(Attr.MapValue(new Dictionary<string, AttributeValue>(3, StringComparer.Ordinal)
            {
                [TypeAttr] = Attr.S(violation.Type.ToString()),
                [ReferenceAttr] = Attr.S(violation.Reference),
                [AtAttr] = Attr.Instant(violation.At),
            }));
        }

        var item = KeyOf(standing.UserId);
        item[DynamoTable.EntityType] = Attr.S(EntityTypeName);
        item[WarningsAttr] = Attr.N(standing.StoredWarnings);
        item[LastWarningAtAttr] = Attr.InstantOrNull(standing.LastWarningAt);
        item[BannedAtAttr] = Attr.InstantOrNull(standing.BannedAt);
        item[RecentAttr] = Attr.ListValue(recent);
        item[VersionAttr] = Attr.N(standing.Version);
        return item;
    }

    internal static AccountStanding ToDomain(Dictionary<string, AttributeValue> item)
    {
        var userId = DynamoTable.UserIdFromPk(item.GetString(DynamoTable.Pk));
        var recent = new List<Violation>();
        foreach (var entry in item.GetList(RecentAttr))
        {
            if (entry.M is { } map
                && Enum.TryParse<ViolationType>(map.GetString(TypeAttr), out var type))
            {
                recent.Add(new Violation(type, map.GetString(ReferenceAttr), map.GetInstant(AtAttr)));
            }
        }

        return AccountStanding.Rehydrate(
            userId,
            item.GetInt(WarningsAttr),
            item.GetInstantOrNull(LastWarningAtAttr),
            item.GetInstantOrNull(BannedAtAttr),
            recent,
            item.GetLongOrDefault(VersionAttr));
    }
}
