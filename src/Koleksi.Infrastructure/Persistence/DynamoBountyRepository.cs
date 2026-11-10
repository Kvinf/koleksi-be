using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Bounties;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores Dicari and their proposals. See <see cref="DynamoTable"/> for the keys: one partition per Dicari, and the board,
/// the sweep, a buyer's list and a seller's proposals each riding an index the table already has.
/// </summary>
/// <remarks>
/// A Dicari is written with <c>UpdateItem</c>, not a whole-item put, because <c>proposalCount</c> lives on the item and is
/// advanced by an atomic add: a put would reset it every time the buyer edited. The sparse index attributes are removed
/// explicitly when the Dicari leaves the board or stops waiting on a clock, which a put's replace semantics would have done
/// for free; that is the price of keeping the counter.
/// </remarks>
internal sealed class DynamoBountyRepository(IAmazonDynamoDB dynamo, DynamoOptions options) : IBountyRepository
{
    /// <summary>Largest page a list reads, so a request cannot ask the table for an unbounded read.</summary>
    private const int MaxPageSize = 100;

    public async Task<Bounty?> FindAsync(string bountyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest { TableName = options.TableName, Key = MetaKey(bountyId), ConsistentRead = true },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? BountyItem.ToDomain(item) : null;
    }

    public async Task AddAsync(Bounty bounty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = BountyItem.ToItem(bounty),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"A Dicari already exists with id '{bounty.Id}'.");
        }
    }

    public async Task UpdateAsync(Bounty bounty, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bounty);

        var update = BuildUpdate(options.TableName, bounty, expectedVersion);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = update.TableName,
                    Key = update.Key,
                    UpdateExpression = update.UpdateExpression,
                    ConditionExpression = update.ConditionExpression,
                    ExpressionAttributeNames = update.ExpressionAttributeNames,
                    ExpressionAttributeValues = update.ExpressionAttributeValues,
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Dicari '{bounty.Id}' changed since it was read.");
        }
    }

    /// <summary>
    /// The conditional write of a Dicari's stored state, as a transaction-ready <see cref="Update"/>. Used by
    /// <see cref="UpdateAsync"/> and by staff removal, which commits it together with its audit entry.
    /// </summary>
    internal static Update BuildUpdate(string tableName, Bounty bounty, long expectedVersion)
    {
        var item = BountyItem.ToItem(bounty);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { ["#v"] = BountyItem.VersionAttribute };
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":expected"] = Attr.N(expectedVersion),
        };

        var sets = new List<string>();
        var removes = new List<string>();
        var n = 0;

        foreach (var (name, value) in item)
        {
            // The key is the item's identity, and the count belongs to the atomic add.
            if (name is DynamoTable.Pk or DynamoTable.Sk or BountyItem.ProposalCountAttr)
            {
                continue;
            }

            var alias = $"#n{n.ToString(CultureInfo.InvariantCulture)}";
            var valueAlias = $":v{n.ToString(CultureInfo.InvariantCulture)}";
            n++;
            names[alias] = name;
            values[valueAlias] = value;
            sets.Add($"{alias} = {valueAlias}");
        }

        // A Dicari that left the board, or stopped waiting on a clock, must lose those index keys.
        foreach (var sparse in BountyItem.SparseIndexAttributes)
        {
            if (!item.ContainsKey(sparse))
            {
                var alias = $"#n{n.ToString(CultureInfo.InvariantCulture)}";
                n++;
                names[alias] = sparse;
                removes.Add(alias);
            }
        }

        var expression = "SET " + string.Join(", ", sets) + (removes.Count > 0 ? " REMOVE " + string.Join(", ", removes) : string.Empty);

        return new Update
        {
            TableName = tableName,
            Key = MetaKey(bounty.Id),
            UpdateExpression = expression,
            ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #v = :expected",
            ExpressionAttributeNames = names,
            ExpressionAttributeValues = values,
        };
    }

    public async Task<IReadOnlyList<Bounty>> ListOpenAsync(string categoryId, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var bounties = new List<Bounty>();
        Dictionary<string, AttributeValue>? startKey = null;
        var queries = 0;

        // A Query on one board partition, newest first. A page can come back short, so it keeps reading until it has
        // enough or the partition ends, but never for more than a few round trips.
        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.BrowseNewestIndex,
                    KeyConditionExpression = $"{DynamoTable.BrowseNewestPk} = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.BountyOpenPkValue(categoryId)),
                    },
                    Limit = Math.Min(limit - bounties.Count, MaxPageSize),
                    ScanIndexForward = false,
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            bounties.AddRange((response.Items ?? []).Select(BountyItem.ToDomain));
            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            queries++;
        }
        while (startKey is not null && bounties.Count < limit && queries < 5);

        return bounties;
    }

    public async Task<int> CountOpenAsync(string categoryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);

        var total = 0;
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.BrowseNewestIndex,
                    KeyConditionExpression = $"{DynamoTable.BrowseNewestPk} = :pk",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.BountyOpenPkValue(categoryId)),
                    },
                    Select = Select.COUNT,
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            total += response.Count ?? 0;
            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey is not null);

        return total;
    }

    public async Task<Page<Bounty>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);

        var response = await dynamo.QueryPageAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.BuyerIndex,
                KeyConditionExpression = $"{DynamoTable.BuyerPk} = :pk AND begins_with({DynamoTable.BuyerSk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.BuyerPkValue(buyerId)),
                    [":prefix"] = Attr.S(DynamoTable.BountyIndexPrefix()),
                },
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                Limit = Math.Clamp(limit, 1, MaxPageSize),
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var items = (response.Items ?? []).Select(BountyItem.ToDomain).ToList();
        return new Page<Bounty>(items, DynamoCursor.Encode(response.LastEvaluatedKey), items.Count);
    }

    public async Task<IReadOnlyList<Bounty>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Fan out over the shards and merge, exactly as the order sweeps do.
        var perShard = Math.Max(1, limit / DynamoTable.AuctionShardCount);
        var queries = new Task<QueryResponse>[DynamoTable.AuctionShardCount];

        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            queries[shard] = dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.AuctionEndIndex,
                    KeyConditionExpression =
                        $"{DynamoTable.AuctionEndPk} = :pk AND {DynamoTable.AuctionEndSk} <= :now",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.BountyDuePkValue(shard)),
                        [":now"] = Attr.S(DynamoTable.EndsAtUpperBound(asOf)),
                    },
                    Limit = perShard,
                    ScanIndexForward = true,
                },
                ct);
        }

        var responses = await Task.WhenAll(queries).ConfigureAwait(false);

        return responses
            .SelectMany(response => response.Items ?? [])
            .Select(BountyItem.ToDomain)
            .OrderBy(bounty => BountyItem.DueAt(bounty) ?? bounty.ExpiresAt)
            .Take(limit)
            .ToList();
    }

    public async Task AddProposalAsync(BountyProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var writes = new List<TransactWriteItem>
        {
            // The Dicari must exist; the count is advanced here, atomically, never from a whole-item write.
            new()
            {
                Update = new Update
                {
                    TableName = options.TableName,
                    Key = MetaKey(proposal.BountyId),
                    UpdateExpression = $"ADD #c :one",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #st = :open "
                        // A Dicari with no accepted proposal stores acceptedProposalId as a NULL value (Attr.SOrNull), and
                        // attribute_not_exists is false for an attribute that exists as NULL, so test for both.
                        + "AND (attribute_not_exists(#ap) OR attribute_type(#ap, :nul))",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#c"] = BountyItem.ProposalCountAttr,
                        ["#st"] = BountyItem.StatusAttrName,
                        ["#ap"] = BountyItem.AcceptedProposalIdAttrName,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":one"] = Attr.N(1),
                        [":open"] = Attr.EnumValue(BountyStatus.Open),
                        [":nul"] = Attr.S("NULL"),
                    },
                },
            },
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = BountyProposalItem.ToItem(proposal),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },

            // The seller's one slot. Present while their proposal is live, so a second concurrent proposal is refused.
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = BountyProposerPointerItem.ToItem(proposal.BountyId, proposal.SellerId, proposal.Id),
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
            },
        };

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct)
                .ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException(
                $"The seller already has a live proposal on Dicari '{proposal.BountyId}'.");
        }
    }

    public async Task UpdateProposalAsync(BountyProposal proposal, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = options.TableName,
                    Item = BountyProposalItem.ToItem(proposal),
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #v = :expected",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#v"] = BountyProposalItem.VersionAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":expected"] = Attr.N(expectedVersion),
                    },
                },
            },
        };

        // Withdrawn and lapsed proposals free the seller's slot so they can propose again. Declined ones keep it, and
        // an accepted one holds it until it lapses. Deleting an absent pointer is not an error.
        if (proposal.Status is ProposalStatus.Withdrawn or ProposalStatus.Lapsed)
        {
            writes.Add(new TransactWriteItem
            {
                Delete = new Delete
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(proposal.BountyId)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.BountyProposerSk(proposal.SellerId)),
                    },
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk}) OR #p = :thisProposal",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#p"] = BountyProposerPointerItem.ProposalIdAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":thisProposal"] = Attr.S(proposal.Id),
                    },
                },
            });
        }

        try
        {
            await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct)
                .ConfigureAwait(false);
        }
        catch (TransactionCanceledException cancelled) when (DynamoTransactionConflicts.IsConflict(cancelled))
        {
            throw new ConcurrencyConflictException($"Proposal '{proposal.Id}' changed since it was read.");
        }
    }

    public async Task<BountyProposal?> FindProposalAsync(string bountyId, string proposalId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bountyId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.BountyProposalSk(proposalId)),
                },
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? BountyProposalItem.ToDomain(item) : null;
    }

    public async Task<IReadOnlyList<BountyProposal>> ListProposalsAsync(string bountyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);

        var proposals = new List<BountyProposal>();
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
                        [":pk"] = Attr.S(DynamoTable.BountyPk(bountyId)),
                        [":prefix"] = Attr.S(DynamoTable.BountyProposalSkPrefixValue()),
                    },
                    ConsistentRead = true,
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            proposals.AddRange((response.Items ?? []).Select(BountyProposalItem.ToDomain));
            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        }
        while (startKey is not null);

        return proposals;
    }

    public async Task<BountyProposal?> FindLiveProposalBySellerAsync(string bountyId, string sellerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var pointer = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bountyId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.BountyProposerSk(sellerId)),
                },
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return pointer.Item is { Count: > 0 } item
            ? await FindProposalAsync(bountyId, item.GetString(BountyProposerPointerItem.ProposalIdAttribute), ct).ConfigureAwait(false)
            : null;
    }

    public async Task<Page<BountyProposal>> ListProposalsForSellerAsync(
        string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var response = await dynamo.QueryPageAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.SellerIndex,
                KeyConditionExpression = $"{DynamoTable.SellerPk} = :pk AND begins_with({DynamoTable.SellerSk}, :prefix)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.SellerPkValue(sellerId)),
                    [":prefix"] = Attr.S(DynamoTable.BountyProposalIndexPrefix()),
                },
                ExclusiveStartKey = DynamoCursor.Decode(cursor),
                Limit = Math.Clamp(limit, 1, MaxPageSize),
                ScanIndexForward = false,
            },
            ct).ConfigureAwait(false);

        var items = (response.Items ?? []).Select(BountyProposalItem.ToDomain).ToList();
        return new Page<BountyProposal>(items, DynamoCursor.Encode(response.LastEvaluatedKey), items.Count);
    }

    private static Dictionary<string, AttributeValue> MetaKey(string bountyId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.BountyPk(bountyId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BountyMetaSk()),
        };
}
