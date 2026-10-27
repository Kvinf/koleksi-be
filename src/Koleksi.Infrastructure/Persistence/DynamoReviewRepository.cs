using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Reviews;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores reviews and each seller's rating counters.
/// </summary>
/// <remarks>
/// A review is one transaction of three writes: the order's review item (conditional on not
/// existing, which is the one-review-per-order guarantee), the seller's feed item, and an
/// <c>UpdateItem ... ADD</c> on the seller's counters. The counters are never rewritten from a
/// read, so two reviews landing together cannot lose each other's increment.
/// </remarks>
internal sealed class DynamoReviewRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IReviewRepository
{
    public async Task<bool> TryAddAsync(Review review, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(review);

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#entity"] = DynamoTable.EntityType,
            ["#count"] = ReviewItem.CountAttribute,
            ["#sum"] = ReviewItem.SumAttribute,
            ["#star"] = ReviewItem.StarAttribute(review.Rating),
        };

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = ReviewItem.ToOrderItem(review),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = ReviewItem.ToFeedItem(review),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = StatsKey(review.SellerId),
                    UpdateExpression = "SET #entity = if_not_exists(#entity, :type) ADD #count :one, #sum :rating, #star :one",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":type"] = Attr.S(ReviewItem.StatsTypeName),
                        [":one"] = Attr.N(1),
                        [":rating"] = Attr.N(review.Rating),
                    },
                },
            },
        };

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes }, ct).ConfigureAwait(false);
            return true;
        }
        catch (TransactionCanceledException cancelled)
            when (cancelled.CancellationReasons is { Count: > 0 } reasons
                && string.Equals(reasons[0].Code, "ConditionalCheckFailed", StringComparison.Ordinal))
        {
            // The order's item already exists: someone reviewed this order first.
            return false;
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            // Another review of this seller is updating the same rating totals at this very
            // instant. That says nothing about whether this order was reviewed, so it is a
            // retryable conflict, not "already reviewed".
            throw new ConcurrencyConflictException(
                $"Reviews for the seller of order '{review.OrderId}' are being updated by another request.");
        }
    }

    public async Task<Review?> FindByOrderAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.OrderReviewPk(orderId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.OrderReviewSk()),
                },

                // The buyer is sent back to the order page straight after submitting.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ReviewItem.ToDomain(item) : null;
    }

    public async Task<Page<Review>> ListBySellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var response = await dynamo.QueryPageAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.SellerReviewsPk(sellerId)),
                    [":prefix"] = Attr.S(DynamoTable.SellerReviewSkPrefix()),
                },

                // Ids are time-ordered: descending is newest first.
                ScanIndexForward = false,
                Limit = limit,

                // The partition is fixed from the seller, never from the cursor (see DynamoCursor),
                // so a forged cursor can only start this seller's own feed somewhere odd.
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
            },
            ct).ConfigureAwait(false);

        var items = (response.Items ?? []).Select(ReviewItem.ToDomain).ToList();
        return new Page<Review>(items, DynamoCursor.Encode(response.LastEvaluatedKey), items.Count);
    }

    public async Task<SellerRatingStats> GetStatsAsync(string sellerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = StatsKey(sellerId) }, ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ReviewItem.ToStats(item) : SellerRatingStats.Empty;
    }

    private static Dictionary<string, AttributeValue> StatsKey(string sellerId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.SellerReviewsPk(sellerId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.SellerReviewStatsSk()),
        };
}
