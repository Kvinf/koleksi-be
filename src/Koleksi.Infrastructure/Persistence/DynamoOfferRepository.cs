using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores offers, alongside the small pointer row <see cref="OfferActivePointerItem"/>
/// that answers "does this buyer already have an open offer on this listing" without a
/// scan.
/// </summary>
/// <remarks>
/// The main row and its pointer are written and cleared together in one transaction —
/// same reasoning as <see cref="DynamoAuctionRepository.SaveBidAsync"/> writing an
/// auction and its bid rows atomically: a status change that updated the offer but left
/// a stale pointer behind would silently let a buyer stack a second offer the pointer was
/// meant to block, or silently block a legitimate new one after the old one resolved.
/// </remarks>
internal sealed class DynamoOfferRepository(IAmazonDynamoDB dynamo, DynamoOptions options) : IOfferRepository
{
    public async Task<Offer?> FindAsync(string offerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(offerId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = KeyOf(offerId) },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? OfferItem.ToDomain(item) : null;
    }

    public async Task AddAsync(Offer offer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = OfferItem.ToItem(offer),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
        };

        // Auto-declined offers (the seller's threshold applied at creation — see
        // Offer.Create) are already terminal and need no pointer to guard against
        // stacking; there is nothing left open to stack against.
        if (OfferItem.IsActiveStatus(offer.Status))
        {
            writes.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = OfferActivePointerItem.ToItem(offer.ListingId, offer.BuyerId, offer.Id),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled)
            when (cancelled.CancellationReasons?.Exists(
                reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) == true)
        {
            throw new ConcurrencyConflictException($"An offer already exists with id '{offer.Id}'.");
        }
    }

    public async Task UpdateAsync(Offer offer, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = OfferItem.ToItem(offer),
                    ConditionExpression = "#v = :expected",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = OfferItem.VersionAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":expected"] = Attr.N(expectedVersion),
                    },
                },
            },
        };

        var pointerKey = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(offer.ListingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OfferActiveSk(offer.BuyerId)),
        };

        writes.Add(new TransactWriteItem
        {
            // Still open (a counter restarts the window but stays active): the pointer's
            // value never actually changes, but it is easiest to reason about as an
            // unconditional overwrite rather than special-casing "no-op when unchanged".
            // Resolved: the pointer is removed, freeing the buyer to make a fresh offer.
            Put = OfferItem.IsActiveStatus(offer.Status)
                ? new Put
                {
                    TableName = options.TableName,
                    Item = OfferActivePointerItem.ToItem(offer.ListingId, offer.BuyerId, offer.Id),
                }
                : null,
            Delete = OfferItem.IsActiveStatus(offer.Status)
                ? null
                : new Delete { TableName = options.TableName, Key = pointerKey },
        });

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled)
            when (cancelled.CancellationReasons?.Exists(
                reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) == true)
        {
            throw new ConcurrencyConflictException($"Offer '{offer.Id}' changed since it was read.");
        }
    }

    public Task<Page<Offer>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct) =>
        QueryIndexAsync(
            DynamoTable.SellerIndex, DynamoTable.SellerPk, DynamoTable.SellerPkValue(sellerId),
            DynamoTable.SellerSk, cursor, limit, ct);

    public Task<Page<Offer>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct) =>
        QueryIndexAsync(
            DynamoTable.BuyerIndex, DynamoTable.BuyerPk, DynamoTable.BuyerPkValue(buyerId),
            DynamoTable.BuyerSk, cursor, limit, ct);

    public async Task<Offer?> FindActiveAsync(string listingId, string buyerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        var pointer = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.OfferActiveSk(buyerId)),
                },
            },
            ct).ConfigureAwait(false);

        if (pointer.Item is not { Count: > 0 } pointerItem)
        {
            return null;
        }

        return await FindAsync(OfferActivePointerItem.GetOfferId(pointerItem), ct).ConfigureAwait(false);
    }

    private async Task<Page<Offer>> QueryIndexAsync(
        string indexName, string pkAttribute, string pkValue, string skAttribute,
        string? cursor, int limit, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = indexName,
                KeyConditionExpression = $"{pkAttribute} = :pk AND begins_with({skAttribute}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(pkValue),
                    [":prefix"] = Attr.S(DynamoTable.OfferIndexPrefix()),
                },
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                Limit = limit,
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var offers = new List<Offer>(response.Items?.Count ?? 0);
        foreach (var item in response.Items ?? [])
        {
            offers.Add(OfferItem.ToDomain(item));
        }

        return new Page<Offer>(offers, DynamoCursor.Encode(response.LastEvaluatedKey), offers.Count);
    }

    private static Dictionary<string, AttributeValue> KeyOf(string offerId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OfferPk(offerId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OfferMetaSk()),
        };
}
