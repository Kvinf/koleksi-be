using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores a buyer's cart as one row per listing in the buyer's own partition.
/// </summary>
/// <remarks>
/// No aggregate and no version here, unlike every other repository in this file. A cart
/// item is an independent fact — "this listing is in this buyer's cart" — so adding or
/// removing one listing can never contend with, or need to be ordered against, a change to
/// another. That is also why <see cref="AddItemAsync"/> is an unconditional put rather than
/// a conditional one: re-adding a listing already present is meant to succeed silently,
/// not race a concurrency check against itself.
/// </remarks>
internal sealed class DynamoCartRepository(IAmazonDynamoDB dynamo, DynamoOptions options) : ICartRepository
{
    private const string AddedAtAttr = "addedAt";
    private const string QuantityAttr = "quantity";

    public async Task AddItemAsync(
        string buyerId, string listingId, DateTimeOffset addedAt, CancellationToken ct, int quantity = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = options.TableName,
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.CartPk(buyerId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.CartItemSk(listingId)),
                    [AddedAtAttr] = Attr.Instant(addedAt),
                    [QuantityAttr] = Attr.N(Math.Max(1, quantity)),
                },
            },
            ct).ConfigureAwait(false);
    }

    public async Task RemoveItemAsync(string buyerId, string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        await dynamo.DeleteItemAsync(
            new DeleteItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.CartPk(buyerId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.CartItemSk(listingId)),
                },
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CartItem>> ListAsync(string buyerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.CartPk(buyerId)),
                    [":prefix"] = Attr.S(DynamoTable.CartItemSkPrefix()),
                },
            },
            ct).ConfigureAwait(false);

        var prefixLength = DynamoTable.CartItemSkPrefix().Length;
        var items = new List<CartItem>(response.Items?.Count ?? 0);
        foreach (var item in response.Items ?? [])
        {
            items.Add(new CartItem(
                item.GetString(DynamoTable.Sk)[prefixLength..],
                item.GetInstant(AddedAtAttr),
                // Rows written before quantities existed are one unit.
                item.TryGetValue(QuantityAttr, out var quantity) && quantity.N is { } quantityText
                    ? Math.Max(1, int.Parse(quantityText, System.Globalization.CultureInfo.InvariantCulture))
                    : 1));
        }

        return items;
    }
}
