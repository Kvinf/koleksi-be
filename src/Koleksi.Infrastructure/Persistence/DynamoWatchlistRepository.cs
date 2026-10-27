using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores a person's saved listings as one row per listing in their own partition, and keeps
/// each listing's <c>watcherCount</c> in step with those rows.
/// </summary>
/// <remarks>
/// <para>
/// Every change is one <c>TransactWriteItems</c> of two writes: the row (put only if absent,
/// delete only if present) and an atomic <c>ADD</c> of +1 or -1 on the listing. Because the row
/// carries the condition, the counter moves exactly when the row did — a second tap on the
/// heart, a retried request, or two tabs racing each other cannot count twice, and an unsave of
/// something never saved cannot count down. The counter is never read back and rewritten, so
/// two people saving the same listing at once both count.
/// </para>
/// <para>
/// The ids are time-ordered, but a row is keyed by listing id, not by when it was saved: a
/// point read or delete needs the key without knowing <c>savedAt</c>. "Newest saved first" is
/// therefore a sort over the person's whole partition, which the ceiling in
/// <see cref="WatchlistRules.MaxItems"/> keeps to a couple of hundred small rows.
/// </para>
/// </remarks>
internal sealed class DynamoWatchlistRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IWatchlistRepository
{
    private const string SavedAtAttr = "savedAt";
    private const string EntityName = "Watch";

    /// <summary>
    /// How many times a transaction is tried when it loses a race on the listing's row.
    /// </summary>
    /// <remarks>
    /// DynamoDB refuses a transaction (rather than queueing it) when another transaction, or a
    /// plain write such as a view count, is touching the same item at that instant. On a popular
    /// listing that is ordinary traffic, not a fault, and the windows are milliseconds wide.
    /// </remarks>
    private const int MaxAttempts = 4;

    public async Task<WatchChange> AddAsync(
        string userId, string listingId, DateTimeOffset savedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.WatchlistPk(userId)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.WatchlistItemSk(listingId)),
                        [DynamoTable.EntityType] = Attr.S(EntityName),
                        [SavedAtAttr] = Attr.Instant(savedAt),
                    },

                    // The whole idempotency story: a row that is already there is not
                    // replaced, so the count below is not bumped a second time.
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = ListingKey(listingId),
                    UpdateExpression = "ADD #watchers :one",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #status = :active",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#watchers"] = ListingItem.WatcherCountAttribute,
                        ["#status"] = ListingItem.StatusAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":one"] = Attr.N(1L),
                        [":active"] = Attr.EnumValue(ListingStatus.Active),
                    },
                },
            },
        };

        var refused = await TransactAsync(writes, ct).ConfigureAwait(false);
        if (refused is null)
        {
            return WatchChange.Changed;
        }

        // Reasons come back in the order the writes were sent. The row is checked first: if it
        // was already there, that is the answer whatever the listing looks like now.
        return refused[0] ? WatchChange.Unchanged : WatchChange.ListingUnavailable;
    }

    public async Task<WatchChange> RemoveAsync(string userId, string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Delete = new Delete
                {
                    TableName = options.TableName,
                    Key = RowKey(userId, listingId),
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                },
            },
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = ListingKey(listingId),
                    UpdateExpression = "ADD #watchers :minusOne",

                    // No status condition: a sold or archived listing must still count down.
                    // The counter must be above zero so a drifted count can never go negative.
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #watchers > :zero",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#watchers"] = ListingItem.WatcherCountAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":minusOne"] = Attr.N(-1L),
                        [":zero"] = Attr.N(0L),
                    },
                },
            },
        };

        var refused = await TransactAsync(writes, ct).ConfigureAwait(false);
        if (refused is null)
        {
            return WatchChange.Changed;
        }

        if (refused[0])
        {
            return WatchChange.Unchanged;
        }

        // The row is there but the listing could not take the decrement: it no longer exists,
        // or its count already reads zero. Either way the person's list must still shed the
        // row — the alternative is a saved item they can never remove.
        try
        {
            await dynamo.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = options.TableName,
                    Key = RowKey(userId, listingId),
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);

            return WatchChange.Changed;
        }
        catch (ConditionalCheckFailedException)
        {
            return WatchChange.Unchanged;
        }
    }

    public async Task<IReadOnlyList<WatchEntry>> ListAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var prefixLength = DynamoTable.WatchlistItemSkPrefix().Length;
        var entries = new List<WatchEntry>();
        Dictionary<string, AttributeValue>? startKey = null;

        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.WatchlistPk(userId)),
                        [":prefix"] = Attr.S(DynamoTable.WatchlistItemSkPrefix()),
                    },
                    ExclusiveStartKey = startKey,

                    // Strongly consistent: the heart a person has just tapped must still be
                    // filled when the page that marks it is read a moment later.
                    ConsistentRead = true,
                },
                ct).ConfigureAwait(false);

            foreach (var item in response.Items ?? [])
            {
                entries.Add(new WatchEntry(
                    item.GetString(DynamoTable.Sk)[prefixLength..],
                    item.GetInstant(SavedAtAttr)));
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
        }
        while (startKey is not null);

        return entries
            .OrderByDescending(entry => entry.SavedAt)
            .ThenByDescending(entry => entry.ListingId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Runs the transaction, retrying when it loses a race on the listing.
    /// </summary>
    /// <returns>
    /// <c>null</c> when it committed. Otherwise one flag per write, in order, saying whether that
    /// write's condition is what refused it.
    /// </returns>
    /// <exception cref="ConcurrencyConflictException">Still contended after every attempt.</exception>
    private async Task<bool[]?> TransactAsync(List<TransactWriteItem> writes, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dynamo.TransactWriteItemsAsync(
                    new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
                return null;
            }
            catch (TransactionCanceledException cancelled)
            {
                var reasons = cancelled.CancellationReasons ?? [];
                var conditionFailed = writes
                    .Select((_, index) => index < reasons.Count
                        && string.Equals(reasons[index].Code, "ConditionalCheckFailed", StringComparison.Ordinal))
                    .ToArray();

                // A failed condition is a definite answer about the state of the data.
                if (conditionFailed.Contains(true))
                {
                    return conditionFailed;
                }

                var contended = reasons.Exists(reason =>
                    string.Equals(reason.Code, "TransactionConflict", StringComparison.Ordinal)
                    || string.Equals(reason.Code, "ThrottlingError", StringComparison.Ordinal)
                    || string.Equals(reason.Code, "ProvisionedThroughputExceeded", StringComparison.Ordinal));

                if (!contended)
                {
                    throw;
                }

                if (attempt >= MaxAttempts)
                {
                    throw new ConcurrencyConflictException(
                        "The listing's watcher count is being updated by other requests.");
                }
            }
            catch (TransactionConflictException) when (attempt < MaxAttempts)
            {
                // The same race, reported as an exception of its own.
            }
            catch (TransactionConflictException)
            {
                throw new ConcurrencyConflictException(
                    "The listing's watcher count is being updated by other requests.");
            }

            // Jittered so the requests that collided do not collide again in lock step.
            await Task.Delay(RandomNumberGenerator.GetInt32(15, 50) * attempt, ct).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, AttributeValue> RowKey(string userId, string listingId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.WatchlistPk(userId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.WatchlistItemSk(listingId)),
        };

    private static Dictionary<string, AttributeValue> ListingKey(string listingId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ListingMetaSk()),
        };
}
