using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Users;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores each account's one profile document in the single table.
/// </summary>
/// <remarks>
/// A plain conditional put for both add and update, unlike <c>DynamoListingRepository</c>'s
/// update: a profile has no externally maintained counter analogous to a listing's view
/// count, so there is no attribute a put would have to be careful not to clobber.
/// </remarks>
internal sealed class DynamoProfileRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IProfileRepository
{
    public async Task<UserProfile?> FindAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(userId),

                // A save must be visible on the very next read — the profile screen
                // reloads onto the just-saved copy after every submit.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ProfileItem.ToDomain(item) : null;
    }

    public async Task AddAsync(UserProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = ProfileItem.ToItem(profile),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Account '{profile.Id}' already has a profile.");
        }
    }

    public async Task UpdateAsync(UserProfile profile, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = ProfileItem.ToItem(profile),
                    ConditionExpression = "#v = :expected",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = ProfileItem.VersionAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":expected"] = Attr.N(expectedVersion),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Profile for '{profile.Id}' changed concurrently.");
        }
    }

    private static Dictionary<string, AttributeValue> KeyOf(string userId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.UserPk(userId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ProfileSk()),
        };
}
