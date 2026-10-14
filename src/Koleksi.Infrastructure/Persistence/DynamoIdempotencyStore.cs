using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Remembers which side-effecting requests have already run.
/// </summary>
/// <remarks>
/// <para>
/// Two sources of duplicates matter. Payment webhooks are delivered at least once by
/// design, and a buyer on a flaky connection retries a checkout they cannot tell
/// succeeded. Neither may produce two charges or two orders.
/// </para>
/// <para>
/// Claims are removed through DynamoDB's TTL rather than a cleanup job, so the table cannot
/// grow without bound and nothing has to remember to prune it. That sweep is lazy (days), so
/// expiry is also enforced here: an expired row can be claimed afresh and is never replayed.
/// </para>
/// </remarks>
internal sealed class DynamoIdempotencyStore(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
    : IIdempotencyStore
{
    private const string ResultAttr = "result";
    private const string ClaimedAtAttr = "claimedAt";
    private const string TtlAttr = "expiresAt";
    private const string LeaseAttr = "leaseUntil";

    /// <summary>
    /// Longest a stored result is worth keeping.
    /// </summary>
    /// <remarks>
    /// Bounds what a caller can ask us to retain. Without a ceiling, a retention of a
    /// hundred years is a request away, and the row is never collected.
    /// </remarks>
    private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(30);

    public async Task<bool> TryClaimAsync(string scope, string key, TimeSpan retention, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (retention <= TimeSpan.Zero || retention > MaxRetention)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retention), $"Retention must be between zero and {MaxRetention.TotalDays} days.");
        }

        var now = clock.UtcNow;

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.IdempotencyPkValue(scope, key)),
                        [DynamoTable.Sk] = Attr.S(ClaimSortKey),
                        [DynamoTable.EntityType] = Attr.S("Idempotency"),
                        [ClaimedAtAttr] = Attr.Instant(now),
                        [TtlAttr] = Attr.N(DynamoTable.ToTtl(now + retention)),
                        [LeaseAttr] = Attr.N((now + IdempotencyExtensions.InFlightLease).ToUnixTimeSeconds()),
                    },

                    // The claim is the conditional write itself. Reading first and then
                    // writing would let two concurrent requests both observe "unclaimed",
                    // which is the exact race this type exists to prevent.
                    //
                    // A claim past its retention counts as absent. DynamoDB's TTL sweep can
                    // lag by days, and without this a re-used key would silently replay an
                    // old result long after it was meant to be forgotten. The put replaces
                    // the whole row, so the stale result goes with it.
                    //
                    // So does a claim that never got a result and has outlived its lease: its
                    // request crashed or timed out (a clean failure releases the claim itself),
                    // and without this its key would answer "in progress" for the whole
                    // retention window. A row written before the lease existed has no lease
                    // attribute, the comparison is false, and it simply waits out its retention.
                    ConditionExpression =
                        $"attribute_not_exists({DynamoTable.Pk}) OR #exp <= :now "
                        + "OR (attribute_not_exists(#result) AND #lease <= :now)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#exp"] = TtlAttr,
                        ["#result"] = ResultAttr,
                        ["#lease"] = LeaseAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":now"] = Attr.N(now.ToUnixTimeSeconds()),
                    },
                },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            // Someone else already claimed it. The caller replays the recorded result.
            return false;
        }
    }

    public async Task RecordResultAsync(string scope, string key, string resultJson, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(resultJson);

        await dynamo.UpdateItemAsync(
            new UpdateItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(scope, key),
                UpdateExpression = "SET #result = :result",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#result"] = ResultAttr,
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":result"] = Attr.S(resultJson),
                },

                // Only the holder of an existing claim may record against it. Without this
                // an unclaimed key could be seeded with a result of the caller's choosing,
                // and the next genuine request would replay it instead of doing the work.
                ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
            },
            ct).ConfigureAwait(false);
    }

    public async Task ReleaseClaimAsync(string scope, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await dynamo.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = options.TableName,
                    Key = KeyOf(scope, key),

                    // A recorded result is the whole point of the row and is never released.
                    ConditionExpression = "attribute_not_exists(#result)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#result"] = ResultAttr,
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // A result exists, so the request finished after all. Keep it.
        }
    }

    public async Task<string?> GetRecordedResultAsync(string scope, string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(scope, key),

                // A replay must see the result written moments ago by the request it is
                // duplicating; an eventually consistent read here would re-run the work.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        if (response.Item is not { Count: > 0 } item)
        {
            return null;
        }

        // An expired row is as good as absent, whether or not the TTL sweep has reached it.
        return item.GetLongOrDefault(TtlAttr) <= clock.UtcNow.ToUnixTimeSeconds()
            ? null
            : item.GetStringOrNull(ResultAttr);
    }

    private const string ClaimSortKey = "#CLAIM";

    private static Dictionary<string, AttributeValue> KeyOf(string scope, string key) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.IdempotencyPkValue(scope, key)),
            [DynamoTable.Sk] = Attr.S(ClaimSortKey),
        };
}
