using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Users;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores public profiles and the handle claims that keep them unique.
/// </summary>
/// <remarks>
/// <para>
/// A handle belongs to one account because its lookup row (<c>HANDLE#{handle}</c>) is written with
/// <c>attribute_not_exists</c> in the same transaction as the account's profile item. Two
/// accounts racing for a name cannot both succeed, and an account cannot be left holding a
/// profile without a claim or a claim without a profile.
/// </para>
/// <para>
/// Renaming moves the claim in one transaction: claim the new handle, release the old one, and
/// rewrite the profile under a version check. The old handle becomes claimable the moment the
/// transaction commits.
/// </para>
/// </remarks>
internal sealed class DynamoPublicProfileRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IPublicProfileRepository
{
    /// <summary><c>BatchGetItem</c> accepts at most 100 keys.</summary>
    private const int BatchSize = 100;

    /// <summary>Retries for keys DynamoDB reports as unprocessed under throttling.</summary>
    private const int MaxBatchRounds = 4;

    public async Task<PublicProfile?> FindByUserAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = PublicProfileItem.KeyOf(userId),

                // A rename or a first assignment must be visible to the very next read: the
                // profile form reloads onto the saved copy, and a lost create race re-reads here.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? PublicProfileItem.ToDomain(item) : null;
    }

    public async Task<IReadOnlyDictionary<string, PublicProfile>> FindManyByUserAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var found = new Dictionary<string, PublicProfile>(StringComparer.Ordinal);
        var distinct = userIds.Distinct(StringComparer.Ordinal).ToList();

        for (var offset = 0; offset < distinct.Count; offset += BatchSize)
        {
            var keys = distinct
                .Skip(offset)
                .Take(BatchSize)
                .Select(PublicProfileItem.KeyOf)
                .ToList();

            var pending = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [options.TableName] = new KeysAndAttributes { Keys = keys },
            };

            for (var round = 0; round < MaxBatchRounds && pending.Count > 0; round++)
            {
                var response = await dynamo.BatchGetItemAsync(
                    new BatchGetItemRequest { RequestItems = pending }, ct).ConfigureAwait(false);

                foreach (var item in response.Responses.GetValueOrDefault(options.TableName) ?? [])
                {
                    var profile = PublicProfileItem.ToDomain(item);
                    found[profile.UserId] = profile;
                }

                pending = response.UnprocessedKeys is { Count: > 0 } ? response.UnprocessedKeys : [];
            }

            if (pending.Count > 0)
            {
                throw new InvalidOperationException("DynamoDB left public-profile keys unprocessed after several retries.");
            }
        }

        return found;
    }

    public async Task<PublicProfile?> FindByHandleAsync(string handle, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);

        var claim = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = PublicProfileItem.HandleKeyOf(handle),
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        if (claim.Item is not { Count: > 0 } claimItem)
        {
            return null;
        }

        // A reserved handle belongs to nobody's storefront: it is the old name of a renamed shop,
        // held back from everyone else for a while, and does not resolve to a page.
        if (claimItem.ContainsKey(PublicProfileItem.ReservedUntilAttribute))
        {
            return null;
        }

        return await FindByUserAsync(claimItem.GetString(PublicProfileItem.UserIdAttribute), ct).ConfigureAwait(false);
    }

    public async Task<PublicProfileWriteResult> CreateAsync(PublicProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = PublicProfileItem.ToItem(profile),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
            ClaimHandle(profile.Handle, profile.UserId, profile.UpdatedAt),
        };

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
            return PublicProfileWriteResult.Written;
        }
        catch (TransactionCanceledException cancelled)
        {
            // Reasons come back in request order: the profile first, then the claim. If the
            // account already has a profile that is the answer, whatever the handle did.
            if (Failed(cancelled, 0))
            {
                return PublicProfileWriteResult.AlreadyExists;
            }

            if (Failed(cancelled, 1))
            {
                return PublicProfileWriteResult.HandleTaken;
            }

            if (DynamoTransactionConflicts.IsConflict(cancelled))
            {
                throw new ConcurrencyConflictException(
                    $"Public profile for '{profile.UserId}' is being created by another request.");
            }

            throw;
        }
    }

    public async Task<PublicProfileWriteResult> SaveAsync(
        PublicProfile profile, string previousHandle, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousHandle);

        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["#v"] = PublicProfileItem.VersionAttribute };
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal) { [":expected"] = Attr.N(expectedVersion) };

        // expectedVersion is the version the caller read, before the edit bumped it, so the
        // stored item must still carry exactly that for the write to land.
        var profileWrite = new TransactWriteItem
        {
            Put = new Put
            {
                TableName = options.TableName,
                Item = PublicProfileItem.ToItem(profile),
                ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #v = :expected",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
            },
        };

        var writes = new List<TransactWriteItem> { profileWrite };
        var renamed = !string.Equals(profile.Handle, previousHandle, StringComparison.Ordinal);

        if (renamed)
        {
            writes.Add(ClaimHandle(profile.Handle, profile.UserId, profile.UpdatedAt));

            // The old handle is reserved, not freed (QA D-10): otherwise anyone could take the old
            // shop URL the moment it was left and trade on its links and reputation. It stays
            // recorded against this account, so only this account can take it back, until the
            // reservation lapses. Only ever touch a claim that is ours.
            writes.Add(new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = PublicProfileItem.HandleKeyOf(previousHandle),
                    UpdateExpression = "SET #until = :until",
                    ConditionExpression = "#owner = :me",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#owner"] = PublicProfileItem.UserIdAttribute,
                        ["#until"] = PublicProfileItem.ReservedUntilAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":me"] = Attr.S(profile.UserId),
                        [":until"] = Attr.Instant(profile.UpdatedAt + PublicProfile.HandleReservationPeriod),
                    },
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
            return PublicProfileWriteResult.Written;
        }
        catch (TransactionCanceledException cancelled)
        {
            if (renamed && Failed(cancelled, 1) && !Failed(cancelled, 0))
            {
                return PublicProfileWriteResult.HandleTaken;
            }

            throw new ConcurrencyConflictException($"Public profile for '{profile.UserId}' changed concurrently.");
        }
    }

    /// <summary>
    /// Claims <paramref name="handle"/> for <paramref name="userId"/>: free, or reserved by this
    /// same account (taking back its own old name), or reserved by anyone whose reservation has
    /// lapsed. A live claim, or somebody else's unexpired reservation, fails the condition.
    /// </summary>
    private TransactWriteItem ClaimHandle(string handle, string userId, DateTimeOffset now) => new()
    {
        Put = new Put
        {
            TableName = options.TableName,
            Item = PublicProfileItem.ToHandleItem(handle, userId),
            ConditionExpression =
                $"attribute_not_exists({DynamoTable.Pk}) OR "
                + "(attribute_exists(#until) AND (#owner = :me OR #until < :now))",
            ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["#owner"] = PublicProfileItem.UserIdAttribute,
                ["#until"] = PublicProfileItem.ReservedUntilAttribute,
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
            {
                [":me"] = Attr.S(userId),
                [":now"] = Attr.Instant(now),
            },
        },
    };

    private static bool Failed(TransactionCanceledException cancelled, int index) =>
        cancelled.CancellationReasons is { } reasons
        && index < reasons.Count
        && string.Equals(reasons[index].Code, "ConditionalCheckFailed", StringComparison.Ordinal);
}
