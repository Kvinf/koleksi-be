using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores auctions and their bid history.
/// </summary>
/// <remarks>
/// <para>
/// This is the only repository that genuinely contends. Everything about its shape
/// follows from that: the auction and its bids live in one partition so they can be
/// written in one transaction, and every write is conditioned on the version that was
/// read.
/// </para>
/// <para>
/// The transaction is not a nicety. An auction whose price moved but whose history row
/// was lost is an auction where the winner is charged an amount no bid accounts for —
/// which is precisely what a losing bidder disputes, and what we would have no evidence
/// to answer.
/// </para>
/// </remarks>
internal sealed class DynamoAuctionRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IAuctionRepository
{
    /// <summary>
    /// DynamoDB's hard ceiling on items in one <c>TransactWriteItems</c> call.
    /// </summary>
    /// <remarks>
    /// A single bid produces at most three items — the auction plus two history rows — so
    /// the limit is never approached in practice. It is asserted anyway, because the
    /// failure mode without the assertion is a transaction rejected at run time in the
    /// busiest minute of an auction.
    /// </remarks>
    private const int MaxTransactionItems = 100;

    public async Task<Auction?> FindAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(listingId),

                // Bidding reads to decide, then writes conditionally. An eventually
                // consistent read here would routinely lose the conditional write and
                // spend a retry to learn what a consistent read would have told it.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? AuctionItem.ToDomain(item) : null;
    }

    public async Task AddAsync(Auction auction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auction);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = AuctionItem.ToItem(auction),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException(
                $"An auction already exists for listing '{auction.ListingId}'.");
        }
    }

    public async Task SaveBidAsync(
        Auction auction, IReadOnlyList<Bid> newBids, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(auction);
        ArgumentNullException.ThrowIfNull(newBids);

        var writes = new List<TransactWriteItem>(newBids.Count + 1)
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = AuctionItem.ToItem(auction),

                    // The whole race is decided here. If another bid landed between our
                    // read and this write, the version no longer matches and the entire
                    // transaction — auction and history rows together — is rejected.
                    ConditionExpression = "#v = :expected",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = AuctionItem.VersionAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":expected"] = Attr.N(expectedVersion),
                    },
                },
            },
        };

        foreach (var bid in newBids)
        {
            writes.Add(new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = BidItem.ToItem(bid),

                    // Bid ids are UUIDv7, so this cannot collide by chance. It guards
                    // against a retried transaction rewriting history that already landed.
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Sk})",
                },
            });
        }

        if (writes.Count > MaxTransactionItems)
        {
            throw new InvalidOperationException(
                $"A single bid produced {writes.Count} writes, above DynamoDB's transaction limit.");
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(
                new TransactWriteItemsRequest { TransactItems = writes },
                ct).ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled)
            when (cancelled.CancellationReasons?.Exists(
                reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) == true)
        {
            // Expected under contention, not a fault. The handler re-reads and re-runs the
            // domain rules against the new price rather than merging, because merging two
            // bids means inventing a price neither bidder offered.
            throw new ConcurrencyConflictException(
                $"Auction '{auction.ListingId}' received another bid first.");
        }
    }

    public async Task<IReadOnlyList<Bid>> GetBidHistoryAsync(
        string listingId, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.ListingPk(listingId)),
                    [":prefix"] = Attr.S(DynamoTable.BidSkPrefix()),
                },
                Limit = limit,

                // Newest first. Bid ids are time-ordered, so the sort key already is.
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var bids = new List<Bid>(response.Items?.Count ?? 0);
        foreach (var item in response.Items ?? [])
        {
            bids.Add(BidItem.ToDomain(item));
        }

        return bids;
    }

    public async Task<IReadOnlyList<Auction>> ListDueForCloseAsync(
        DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The index is sharded, so the sweep fans out and merges. Querying the shards
        // sequentially would make the sweep's latency the sum of four round trips for no
        // reason; querying them in parallel makes it the slowest of the four.
        var perShard = Math.Max(1, limit / DynamoTable.AuctionShardCount);
        var queries = new Task<QueryResponse>[DynamoTable.AuctionShardCount];

        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            queries[shard] = dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,

                    // Requires the index to project ALL attributes — the sweep rehydrates a
                    // full aggregate from what it reads. See infra/: changing that
                    // projection would make this return auctions with a zeroed price.
                    IndexName = DynamoTable.AuctionEndIndex,
                    KeyConditionExpression =
                        $"{DynamoTable.AuctionEndPk} = :pk AND {DynamoTable.AuctionEndSk} <= :now",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(
                            DynamoTable.AuctionEndPkValue(nameof(AuctionStatus.Live), shard)),

                        // The sort key is "{instant}#{listingId}", so an inclusive bound on
                        // the instant needs a suffix that sorts above every id. Comparing
                        // against the bare instant would silently drop an auction ending on
                        // exactly this tick — a rounding-shaped bug that only appears under
                        // a clock that lands on the boundary.
                        [":now"] = Attr.S(DynamoTable.EndsAtUpperBound(asOf)),
                    },
                    Limit = perShard,

                    // Oldest close time first: the auction that ended longest ago is the
                    // one whose winner has been waiting longest.
                    ScanIndexForward = true,
                },
                ct);
        }

        var responses = await Task.WhenAll(queries).ConfigureAwait(false);

        var due = new List<Auction>();
        foreach (var response in responses)
        {
            foreach (var item in response.Items ?? [])
            {
                due.Add(AuctionItem.ToDomain(item));
            }
        }

        // Merge the shards back into one close-time order, then honour the caller's limit.
        due.Sort((left, right) => left.EndsAt.CompareTo(right.EndsAt));
        return due.Count > limit ? due.GetRange(0, limit) : due;
    }

    private static Dictionary<string, AttributeValue> KeyOf(string listingId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.AuctionSk()),
        };
}
