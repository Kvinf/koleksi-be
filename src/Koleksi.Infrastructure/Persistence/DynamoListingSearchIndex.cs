using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Catalog;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Serves browse and search from the DynamoDB projection.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is honestly capable of.</b> DynamoDB sorts and filters; it does not
/// search. Sorting is exact, because each sort order has its own index. Filtering is
/// exact, but happens after the read, so a heavily filtered query pays to read rows it
/// discards. Free text is a case-normalised substring match — no tokens, no stemming, no
/// ranking — and "relevance" therefore falls back to newest-first.
/// </para>
/// <para>
/// <b>Counts are approximate, and the interface says so.</b> Facet counts come from
/// maintained counters scoped to a category, so they answer "how many active PSA 10 cards
/// are there" rather than "how many of the results you are currently looking at are PSA
/// 10". Producing the latter means counting a filtered set on every keystroke, which is
/// what a search engine is for. Over-counting a facet is a visibly imperfect number;
/// under-counting would hide results, so the approximation errs high on purpose.
/// </para>
/// <para>
/// <b>The migration path is why this is a separate interface.</b> Every limitation above
/// disappears behind an OpenSearch implementation of <see cref="IListingSearchIndex"/>
/// with no change to a handler or an endpoint.
/// </para>
/// </remarks>
internal sealed partial class DynamoListingSearchIndex(
    IAmazonDynamoDB dynamo,
    ICategoryRepository categories,
    DynamoOptions options,
    ILogger<DynamoListingSearchIndex> logger) : IListingSearchIndex
{
    /// <summary>
    /// Ceiling on round trips while walking to a deep page.
    /// </summary>
    /// <remarks>
    /// Numbered paging over DynamoDB means walking from the first page, and a filter that
    /// matches almost nothing makes that walk long. The walk is already bounded by
    /// <see cref="BrowseQuery.MaxPageNumber"/>; this bounds it a second time in requests,
    /// so one pathological query cannot hold a Lambda open for its whole timeout.
    /// </remarks>
    private const int MaxQueryRoundTrips = 20;

    public async Task<NumberedPage<ListingSummary>> SearchAsync(BrowseQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.CategoryId is not { } categoryId)
        {
            // Cross-category search fans out over the category partitions rather than
            // reading a single "everything" partition, which would be a write hotspot.
            return await SearchAllCategoriesAsync(query, ct).ConfigureAwait(false);
        }

        var category = await categories.FindByIdAsync(categoryId, ct).ConfigureAwait(false);
        if (category is null)
        {
            return NumberedPage.Empty<ListingSummary>(query.PageSize);
        }

        var matches = await ReadUpToPageAsync(query, categoryId, ct).ConfigureAwait(false);
        var total = await EstimateTotalAsync(query, categoryId, ct).ConfigureAwait(false);

        var skip = (query.PageNumber - 1) * query.PageSize;
        var page = new List<ListingSummary>(query.PageSize);
        for (var i = skip; i < matches.Count && page.Count < query.PageSize; i++)
        {
            page.Add(BrowseProjectionItem.ToSummary(matches[i], category.Slug));
        }

        return new NumberedPage<ListingSummary>(
            page,
            query.PageNumber,
            query.PageSize,

            // Never report a total below what we have actually seen. The counters can lag
            // a write, and "showing 13–24 of 8" is a bug the reader can see.
            Math.Max(total, matches.Count));
    }

    public async Task<IReadOnlyList<FacetCount>> CountFacetsAsync(BrowseQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.CategoryId is not { } categoryId)
        {
            // The sidebar renders per-category facets, and "all categories" has no facet
            // schema to render. Returning nothing is the honest answer.
            return [];
        }

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.FacetPkValue(categoryId)),
                },
                ConsistentRead = false,
            },
            ct).ConfigureAwait(false);

        var counts = new List<FacetCount>(response.Items?.Count ?? 0);
        foreach (var item in response.Items ?? [])
        {
            var sortKey = item.GetString(DynamoTable.Sk);

            // "{facetKey}#{value}". A value may itself contain '#', so split once only.
            var separator = sortKey.IndexOf('#', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var count = item.GetLongOrDefault(FacetCounterAttr);
            if (count > 0)
            {
                counts.Add(new FacetCount(
                    sortKey[..separator],
                    sortKey[(separator + 1)..],
                    count));
            }
        }

        return counts;
    }

    public async Task IndexAsync(ListingIndexEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var listing = entry.Listing;

        // Drafts, archived, and suspended listings are absent from browse entirely rather
        // than present-and-filtered. A row that exists but must never be returned is one
        // forgotten filter expression away from being returned.
        if (!listing.IsPubliclyVisible)
        {
            await RemoveAsync(listing.Id, ct).ConfigureAwait(false);
            return;
        }

        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = options.TableName,
                Item = BrowseProjectionItem.ToItem(
                    listing, entry.SellerUsername, entry.AuctionEndsAt, entry.BidCount),
            },
            ct).ConfigureAwait(false);

        await AdjustFacetCountersAsync(listing, delta: 1, ct).ConfigureAwait(false);
    }

    public async Task UpdatePriceAsync(string listingId, long currentPriceRupiah, int bidCount, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.BrowseSk()),
                    },
                    UpdateExpression = "SET #price = :price, #bidCount = :bidCount, #priceSk = :priceSk",
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#price"] = BrowseProjectionItem.PriceAttr,
                        ["#bidCount"] = BrowseProjectionItem.BidCountAttr,
                        ["#priceSk"] = DynamoTable.BrowsePriceSk,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":price"] = Attr.N(currentPriceRupiah),
                        [":bidCount"] = Attr.N(bidCount),
                        [":priceSk"] = Attr.S(DynamoTable.BrowsePriceSkValue(currentPriceRupiah, listingId)),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // Nothing indexed for this listing yet — a bid landing before the listing's
            // own publish write has settled. The next full IndexAsync catches it up.
        }
    }

    public async Task RemoveAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var response = await dynamo.DeleteItemAsync(
            new DeleteItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.BrowseSk()),
                },

                // The old row is returned so its facet values can be decremented. Without
                // it the counters only ever grow, and the sidebar slowly fills with facets
                // that match nothing.
                ReturnValues = ReturnValue.ALL_OLD,
            },
            ct).ConfigureAwait(false);

        if (response.Attributes is { Count: > 0 } removed)
        {
            await AdjustFacetCountersFromItemAsync(removed, delta: -1, ct).ConfigureAwait(false);
        }
    }

    // ─── Reading ────────────────────────────────────────────────────────────────

    private async Task<NumberedPage<ListingSummary>> SearchAllCategoriesAsync(
        BrowseQuery query, CancellationToken ct)
    {
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);

        // Read enough from each category to fill the requested page from any mix of them,
        // then merge. Correct because the merge re-sorts; bounded because each fan-out leg
        // obeys the same page ceiling as a single-category query.
        var perCategory = new Task<List<Dictionary<string, AttributeValue>>>[all.Count];
        for (var i = 0; i < all.Count; i++)
        {
            perCategory[i] = ReadUpToPageAsync(query, all[i].Id, ct);
        }

        var results = await Task.WhenAll(perCategory).ConfigureAwait(false);

        var slugById = new Dictionary<string, string>(all.Count, StringComparer.Ordinal);
        foreach (var category in all)
        {
            slugById[category.Id] = category.Slug;
        }

        var merged = new List<Dictionary<string, AttributeValue>>();
        foreach (var result in results)
        {
            merged.AddRange(result);
        }

        merged.Sort(ComparerFor(query.Sort));

        var skip = (query.PageNumber - 1) * query.PageSize;
        var page = new List<ListingSummary>(query.PageSize);
        for (var i = skip; i < merged.Count && page.Count < query.PageSize; i++)
        {
            var item = merged[i];
            var slug = slugById.GetValueOrDefault(item.GetString(BrowseProjectionItem.CategoryIdAttr), string.Empty);
            page.Add(BrowseProjectionItem.ToSummary(item, slug));
        }

        return new NumberedPage<ListingSummary>(page, query.PageNumber, query.PageSize, merged.Count);
    }

    /// <summary>
    /// Reads forward until the requested page is covered, or the index runs out.
    /// </summary>
    /// <remarks>
    /// DynamoDB has no offset. A numbered page therefore costs a walk from the first page,
    /// which is exactly why <see cref="BrowseQuery.MaxPageNumber"/> exists: it turns an
    /// unbounded cost into a known one, and no human browses to page 900 — only crawlers
    /// try, and the frontend does not emit links that far.
    /// </remarks>
    private async Task<List<Dictionary<string, AttributeValue>>> ReadUpToPageAsync(
        BrowseQuery query, string categoryId, CancellationToken ct)
    {
        var wanted = query.PageNumber * query.PageSize;
        var collected = new List<Dictionary<string, AttributeValue>>(wanted);

        var (indexName, partitionKeyAttr, partitionKeyValue, ascending) = PlanFor(query.Sort, categoryId);
        var (filter, names, values) = BuildFilter(query);
        values[":pk"] = Attr.S(partitionKeyValue);

        Dictionary<string, AttributeValue>? startKey = null;

        for (var roundTrip = 0; roundTrip < MaxQueryRoundTrips; roundTrip++)
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = indexName,
                    KeyConditionExpression = $"{partitionKeyAttr} = :pk",
                    FilterExpression = filter,
                    ExpressionAttributeNames = names.Count > 0 ? names : null,
                    ExpressionAttributeValues = values,
                    ExclusiveStartKey = startKey,
                    ScanIndexForward = ascending,
                },
                ct).ConfigureAwait(false);

            collected.AddRange(response.Items ?? []);

            startKey = response.LastEvaluatedKey is { Count: > 0 } next ? next : null;
            if (startKey is null || collected.Count >= wanted)
            {
                return collected;
            }
        }

        // The walk was cut short. The page returned is still correct as far as it goes,
        // and it is worth knowing when a filter is this unselective.
        LogWalkTruncated(logger, categoryId, query.PageNumber, MaxQueryRoundTrips);
        return collected;
    }

    /// <summary>Picks the index that already holds the requested order.</summary>
    private static (string Index, string PartitionAttr, string PartitionValue, bool Ascending) PlanFor(
        BrowseSort sort, string categoryId) => sort switch
        {
            BrowseSort.PriceAscending => (
                DynamoTable.BrowsePriceIndex,
                DynamoTable.BrowsePricePk,
                DynamoTable.BrowsePkValue(categoryId),
                true),

            BrowseSort.PriceDescending => (
                DynamoTable.BrowsePriceIndex,
                DynamoTable.BrowsePricePk,
                DynamoTable.BrowsePkValue(categoryId),
                false),

            BrowseSort.AuctionsEndingSoon => (
                DynamoTable.BrowseEndingIndex,
                DynamoTable.BrowseEndingPk,
                DynamoTable.BrowseEndingPkValue(categoryId),
                true),

            // Newest, and relevance — which has nothing to rank by here. Ids are
            // time-ordered, so descending on the sort key is newest first.
            _ => (
                DynamoTable.BrowseNewestIndex,
                DynamoTable.BrowseNewestPk,
                DynamoTable.BrowsePkValue(categoryId),
                false),
        };

    private static Comparison<Dictionary<string, AttributeValue>> ComparerFor(BrowseSort sort) => sort switch
    {
        BrowseSort.PriceAscending => (left, right) =>
            left.GetLong(BrowseProjectionItem.PriceAttr).CompareTo(right.GetLong(BrowseProjectionItem.PriceAttr)),

        BrowseSort.PriceDescending => (left, right) =>
            right.GetLong(BrowseProjectionItem.PriceAttr).CompareTo(left.GetLong(BrowseProjectionItem.PriceAttr)),

        BrowseSort.AuctionsEndingSoon => (left, right) => string.CompareOrdinal(
            left.GetStringOrNull(BrowseProjectionItem.AuctionEndsAtAttr),
            right.GetStringOrNull(BrowseProjectionItem.AuctionEndsAtAttr)),

        // Ids are time-ordered, so an ordinal descending compare is newest first.
        _ => (left, right) => string.CompareOrdinal(
            right.GetString(BrowseProjectionItem.IdAttr),
            left.GetString(BrowseProjectionItem.IdAttr)),
    };

    /// <summary>
    /// Builds the post-read filter.
    /// </summary>
    /// <remarks>
    /// Every value is bound as an expression placeholder and every attribute name goes
    /// through <c>ExpressionAttributeNames</c>. Concatenating a caller's facet value into
    /// the expression string would be injection into the expression language — the same
    /// class of bug as string-built SQL, and just as exploitable.
    /// </remarks>
    private static (string? Filter, Dictionary<string, string> Names, Dictionary<string, AttributeValue> Values)
        BuildFilter(BrowseQuery query)
    {
        var clauses = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);

        // Only live listings are purchasable. Sold ones stay in the table so their links
        // resolve, but they do not belong in a result grid.
        names["#status"] = BrowseProjectionItem.StatusAttr;
        values[":active"] = Attr.S(nameof(ListingStatus.Active));
        clauses.Add("#status = :active");

        if (query.MinPriceRupiah is { } min)
        {
            names["#price"] = BrowseProjectionItem.PriceAttr;
            values[":minPrice"] = Attr.N(min);
            clauses.Add("#price >= :minPrice");
        }

        if (query.MaxPriceRupiah is { } max)
        {
            names["#price"] = BrowseProjectionItem.PriceAttr;
            values[":maxPrice"] = Attr.N(max);
            clauses.Add("#price <= :maxPrice");
        }

        if (!string.IsNullOrWhiteSpace(query.City))
        {
            names["#city"] = BrowseProjectionItem.CityAttr;
            values[":city"] = Attr.S(query.City);
            clauses.Add("#city = :city");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            names["#search"] = BrowseProjectionItem.SearchTextAttr;
            values[":term"] = Attr.S(query.SearchTerm.ToLowerInvariant());
            clauses.Add("contains(#search, :term)");
        }

        var facetIndex = 0;
        foreach (var (facetKey, selected) in query.SelectedFacets)
        {
            if (selected.Count == 0)
            {
                continue;
            }

            var nameToken = $"#f{facetIndex.ToString(CultureInfo.InvariantCulture)}";
            names[nameToken] = BrowseProjectionItem.FacetAttr(facetKey);

            // Values within one facet are OR'd (a shopper wants PSA 9 *or* PSA 10);
            // separate facets are AND'd (PSA 10 *and* Base Set).
            var alternatives = new List<string>(selected.Count);
            for (var i = 0; i < selected.Count; i++)
            {
                var valueToken = $":f{facetIndex.ToString(CultureInfo.InvariantCulture)}v{i.ToString(CultureInfo.InvariantCulture)}";
                values[valueToken] = Attr.S(selected[i]);
                alternatives.Add($"{nameToken} = {valueToken}");
            }

            clauses.Add($"({string.Join(" OR ", alternatives)})");
            facetIndex++;
        }

        return (string.Join(" AND ", clauses), names, values);
    }

    /// <summary>
    /// Estimates the total for the result-count line and the page count.
    /// </summary>
    /// <remarks>
    /// The tightest maintained counter that applies, which is an upper bound: price, city,
    /// and free text are not counted, so a filtered set reads high. Erring high keeps every
    /// result reachable — an under-count would shorten the pager and hide listings a seller
    /// has paid to show.
    /// </remarks>
    private async Task<long> EstimateTotalAsync(BrowseQuery query, string categoryId, CancellationToken ct)
    {
        var counts = await CountFacetsAsync(query with { CategoryId = categoryId }, ct).ConfigureAwait(false);
        if (counts.Count == 0)
        {
            return 0;
        }

        long? tightest = null;
        foreach (var (facetKey, selected) in query.SelectedFacets)
        {
            if (selected.Count == 0)
            {
                continue;
            }

            long forThisFacet = 0;
            foreach (var count in counts)
            {
                if (string.Equals(count.FacetKey, facetKey, StringComparison.Ordinal)
                    && selected.Contains(count.Value, StringComparer.Ordinal))
                {
                    forThisFacet += count.Count;
                }
            }

            tightest = tightest is { } current ? Math.Min(current, forThisFacet) : forThisFacet;
        }

        if (tightest is { } bound)
        {
            return bound;
        }

        // No facets selected: the category total is the sum over any one facet's options,
        // so use the dedicated total counter instead of guessing from a partial sum.
        return await ReadCategoryTotalAsync(categoryId, ct).ConfigureAwait(false);
    }

    private async Task<long> ReadCategoryTotalAsync(string categoryId, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.FacetPkValue(categoryId)),
                    [DynamoTable.Sk] = Attr.S(TotalCounterSortKey),
                },
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? item.GetLongOrDefault(FacetCounterAttr) : 0;
    }

    // ─── Counters ───────────────────────────────────────────────────────────────

    private const string FacetCounterAttr = "count";
    private const string TotalCounterSortKey = "#TOTAL";

    private Task AdjustFacetCountersAsync(Listing listing, long delta, CancellationToken ct)
    {
        var updates = new List<Task>(listing.Attributes.Count + 1)
        {
            AdjustCounterAsync(listing.CategoryId, TotalCounterSortKey, delta, ct),
        };

        foreach (var (facetKey, value) in listing.Attributes)
        {
            updates.Add(AdjustCounterAsync(
                listing.CategoryId, DynamoTable.FacetSkValue(facetKey, value), delta, ct));
        }

        return Task.WhenAll(updates);
    }

    private Task AdjustFacetCountersFromItemAsync(
        Dictionary<string, AttributeValue> item, long delta, CancellationToken ct)
    {
        if (item.GetStringOrNull(BrowseProjectionItem.CategoryIdAttr) is not { } categoryId)
        {
            return Task.CompletedTask;
        }

        var updates = new List<Task> { AdjustCounterAsync(categoryId, TotalCounterSortKey, delta, ct) };

        foreach (var (attribute, value) in item)
        {
            if (attribute.StartsWith(BrowseProjectionItem.FacetPrefix, StringComparison.Ordinal)
                && value.S is { } facetValue)
            {
                updates.Add(AdjustCounterAsync(
                    categoryId,
                    DynamoTable.FacetSkValue(attribute[BrowseProjectionItem.FacetPrefix.Length..], facetValue),
                    delta,
                    ct));
            }
        }

        return Task.WhenAll(updates);
    }

    /// <summary>
    /// Applies an atomic delta to one counter.
    /// </summary>
    /// <remarks>
    /// <c>ADD</c>, so concurrent publishes both count. A failure is logged and swallowed:
    /// a counter is a display number, and a sidebar showing 41 instead of 42 must never be
    /// the reason a seller's listing fails to publish.
    /// </remarks>
    private async Task AdjustCounterAsync(string categoryId, string sortKey, long delta, CancellationToken ct)
    {
        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.FacetPkValue(categoryId)),
                        [DynamoTable.Sk] = Attr.S(sortKey),
                    },
                    UpdateExpression = "ADD #count :delta",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#count"] = FacetCounterAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":delta"] = Attr.N(delta),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (AmazonDynamoDBException failure)
        {
            LogCounterFailed(logger, categoryId, sortKey, failure);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Browse walk for category {CategoryId} page {PageNumber} hit the {Limit}-request ceiling.")]
    private static partial void LogWalkTruncated(
        ILogger logger, string categoryId, int pageNumber, int limit);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Facet counter {SortKey} for category {CategoryId} could not be adjusted.")]
    private static partial void LogCounterFailed(
        ILogger logger, string categoryId, string sortKey, Exception failure);
}
