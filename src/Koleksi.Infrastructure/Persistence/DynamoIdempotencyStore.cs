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
/// Claims expire through DynamoDB's TTL rather than a cleanup job, so the table cannot
/// grow without bound and nothing has to remember to prune it.
/// </para>
/// </remarks>
internal sealed class DynamoIdempotencyStore(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
    : IIdempotencyStore
{
    private const string ResultAttr = "result";
    private const string ClaimedAtAttr = "claimedAt";
    private const string TtlAttr = "expiresAt";

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
                    },

                    // The claim is the conditional write itself. Reading first and then
                    // writing would let two concurrent requests both observe "unclaimed",
                    // which is the exact race this type exists to prevent.
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
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

        return response.Item is { Count: > 0 } item ? item.GetStringOrNull(ResultAttr) : null;
    }

    private const string ClaimSortKey = "#CLAIM";

    private static Dictionary<string, AttributeValue> KeyOf(string scope, string key) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.IdempotencyPkValue(scope, key)),
            [DynamoTable.Sk] = Attr.S(ClaimSortKey),
        };
}
