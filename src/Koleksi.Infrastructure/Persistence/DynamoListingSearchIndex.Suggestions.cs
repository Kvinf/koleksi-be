using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Catalog;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Search suggestions: one small row per distinct word of a live listing's title.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> <c>pk = SUGGEST#{first two letters of the word}</c>, <c>sk = {word}#{listingId}</c>, plus
/// the listing id and the normalised title words (<c>t</c>). A lookup reads one partition with
/// <c>begins_with(sk, typedWord)</c> and a hard item limit, so it is one Query of a few read units no
/// matter how large the catalogue grows, and never a Scan. The rows carry no price, photo or status:
/// the few listings that win are then read from the browse projection, so what is shown is always as
/// fresh as the results page, and a sold or archived listing cannot be shown even if its rows linger.
/// </para>
/// <para>
/// <b>Cost.</b> Idle cost is zero (it is rows in the existing on-demand table). A publish or edit
/// writes one small item per distinct title word, usually 4 to 8, in a single batch; a bid writes
/// nothing here. A lookup is one Query (at most 60 small items) and one BatchGet of at most five
/// listings.
/// </para>
/// <para>
/// <b>Derived data.</b> A failure writing these rows is logged and swallowed: the seller's publish
/// must not fail because an autocomplete row could not be stored. The backfill repairs any gap.
/// </para>
/// </remarks>
internal sealed partial class DynamoListingSearchIndex
{
    /// <summary>Most title words stored in <c>t</c>, which is what typed words are matched against.</summary>
    private const int MaxTitleWords = 16;

    /// <summary>
    /// Most words that get a row of their own. With the same number of deletes for a renamed listing a
    /// single write batch (limit 25) always suffices.
    /// </summary>
    private const int MaxRowWords = 12;

    private const int MinRowWordLength = 2;

    /// <summary>Rows read per lookup. The ceiling on what one keystroke can cost.</summary>
    private const int CandidateLimit = 60;

    /// <summary>Newest listings read from a category to top up a lookup.</summary>
    private const int CategoryTopUpRead = 8;

    private const string SuggestTitleAttr = "t";
    private const string SuggestListingAttr = "listingId";
    private const string SuggestTypeName = "Suggest";

    public async Task<SuggestionMatches> FindAsync(SuggestionLookup lookup, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        if (lookup.Words.Count == 0 || lookup.Words[^1].Length < MinRowWordLength)
        {
            return SuggestionMatches.None;
        }

        var typedLast = lookup.Words[^1];

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                KeyConditionExpression = $"{DynamoTable.Pk} = :pk AND begins_with({DynamoTable.Sk}, :word)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.SuggestPkValue(typedLast)),
                    [":word"] = Attr.S(typedLast),
                },

                // Newest ids first within a word. A suggestion lookup is allowed to be eventually consistent.
                ScanIndexForward = false,
                Limit = CandidateLimit,
                ConsistentRead = false,
            },
            ct).ConfigureAwait(false);

        var candidates = new List<SuggestionCandidate>(response.Items?.Count ?? 0);
        foreach (var row in response.Items ?? [])
        {
            if (row.GetStringOrNull(SuggestListingAttr) is { } id && row.GetStringOrNull(SuggestTitleAttr) is { } title)
            {
                candidates.Add(new SuggestionCandidate(id, title.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            }
        }

        var ranked = SuggestionRanking.Rank(lookup.Words, candidates, lookup.MaxItems);
        var terms = SuggestionRanking.Terms(lookup.Words, candidates, lookup.MaxTerms);

        var summaries = new List<ListingSummary>(lookup.MaxItems);
        var slugById = await CategorySlugsAsync(ct).ConfigureAwait(false);

        foreach (var summary in await ReadLiveSummariesAsync(ranked.Select(c => c.Id), slugById, ct).ConfigureAwait(false))
        {
            summaries.Add(summary);
        }

        // Top up with the newest of a category the visitor named, so typing a category shows what is in it.
        for (var i = 0; i < lookup.SupplementCategoryIds.Count && summaries.Count < lookup.MaxItems; i++)
        {
            foreach (var extra in await NewestLiveAsync(lookup.SupplementCategoryIds[i], slugById, ct).ConfigureAwait(false))
            {
                if (summaries.Count < lookup.MaxItems && summaries.All(existing => existing.Id != extra.Id))
                {
                    summaries.Add(extra);
                }
            }
        }

        return new SuggestionMatches(summaries, terms);
    }

    public async Task<SuggestionBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);

        var categoryIndex = 0;
        string? after = null;

        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = cursor.Split('|', 2);
            categoryIndex = all.ToList().FindIndex(c => string.Equals(c.Id, parts[0], StringComparison.Ordinal));
            after = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        }

        if (categoryIndex < 0 || categoryIndex >= all.Count)
        {
            return new SuggestionBackfillPage(0, null);
        }

        var category = all[categoryIndex];

        // DynamoDB refuses a name or value the expression does not use, so the "after" half is
        // added only when there is a cursor to continue from.
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#pk"] = DynamoTable.BrowseNewestPk,
        };
        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":pk"] = Attr.S(DynamoTable.BrowsePkValue(category.Id)),
        };

        if (after is not null)
        {
            names["#sk"] = DynamoTable.BrowseNewestSk;
            values[":after"] = Attr.S(after);
        }

        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.BrowseNewestIndex,
                KeyConditionExpression = after is null ? "#pk = :pk" : "#pk = :pk AND #sk < :after",
                ExpressionAttributeNames = names,
                ExpressionAttributeValues = values,
                ScanIndexForward = false,
                Limit = limit,
            },
            ct).ConfigureAwait(false);

        var items = response.Items ?? [];
        var indexed = 0;
        string? lastId = null;

        foreach (var item in items)
        {
            lastId = item.GetString(BrowseProjectionItem.IdAttr);

            if (string.Equals(item.GetStringOrNull(BrowseProjectionItem.StatusAttr), nameof(ListingStatus.Active), StringComparison.Ordinal))
            {
                await SyncSuggestionRowsAsync(lastId, oldTitle: null, item.GetString(BrowseProjectionItem.TitleAttr), ct)
                    .ConfigureAwait(false);
                indexed++;
            }
        }

        string? next;
        if (items.Count >= limit && lastId is not null)
        {
            next = $"{category.Id}|{lastId}";
        }
        else
        {
            next = categoryIndex + 1 < all.Count ? $"{all[categoryIndex + 1].Id}|" : null;
        }

        return new SuggestionBackfillPage(indexed, next);
    }

    // ─── Keeping the rows correct ───────────────────────────────────────────────

    /// <summary>
    /// Makes the stored rows for one listing match its current title: writes a row for every word it
    /// has now and deletes the rows of words it had before and no longer does.
    /// </summary>
    /// <param name="newTitle"><c>null</c> removes every row (archived, sold, closed, removed).</param>
    private async Task SyncSuggestionRowsAsync(string listingId, string? oldTitle, string? newTitle, CancellationToken ct)
    {
        var oldWords = RowWords(oldTitle);
        var newWords = RowWords(newTitle);

        var requests = new List<WriteRequest>(oldWords.Count + newWords.Count);

        foreach (var word in oldWords)
        {
            if (!newWords.Contains(word, StringComparer.Ordinal))
            {
                requests.Add(new WriteRequest
                {
                    DeleteRequest = new DeleteRequest
                    {
                        Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [DynamoTable.Pk] = Attr.S(DynamoTable.SuggestPkValue(word)),
                            [DynamoTable.Sk] = Attr.S(DynamoTable.SuggestSkValue(word, listingId)),
                        },
                    },
                });
            }
        }

        if (newTitle is not null)
        {
            var stored = string.Join(' ', Slug.Words(newTitle).Take(MaxTitleWords));

            foreach (var word in newWords)
            {
                requests.Add(new WriteRequest
                {
                    PutRequest = new PutRequest
                    {
                        Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                        {
                            [DynamoTable.Pk] = Attr.S(DynamoTable.SuggestPkValue(word)),
                            [DynamoTable.Sk] = Attr.S(DynamoTable.SuggestSkValue(word, listingId)),
                            [DynamoTable.EntityType] = Attr.S(SuggestTypeName),
                            [SuggestListingAttr] = Attr.S(listingId),
                            [SuggestTitleAttr] = Attr.S(stored),
                        },
                    },
                });
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        try
        {
            var pending = new Dictionary<string, List<WriteRequest>>(StringComparer.Ordinal)
            {
                [options.TableName] = requests,
            };

            // BatchWriteItem may hand back part of the batch under throttling; a few bounded rounds
            // mop that up, and anything left is reported rather than retried forever.
            for (var round = 0; round < 3 && pending.Count > 0; round++)
            {
                var response = await dynamo.BatchWriteItemAsync(
                    new BatchWriteItemRequest { RequestItems = pending }, ct).ConfigureAwait(false);

                pending = response.UnprocessedItems is { Count: > 0 } left ? left : [];
            }

            if (pending.Count > 0)
            {
                LogSuggestionRowsIncomplete(logger, listingId);
            }
        }
        catch (AmazonDynamoDBException failure)
        {
            LogSuggestionRowsFailed(logger, listingId, failure);
        }
    }

    /// <summary>The distinct words of a title that get a row: two letters or more, at most <see cref="MaxRowWords"/>.</summary>
    private static List<string> RowWords(string? title) =>
        [.. Slug.Words(title)
            .Take(MaxTitleWords)
            .Where(word => word.Length >= MinRowWordLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxRowWords)];

    // ─── Reading the winners ────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> CategorySlugsAsync(CancellationToken ct)
    {
        var all = await categories.GetAllAsync(ct).ConfigureAwait(false);
        return all.ToDictionary(category => category.Id, category => category.Slug, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads the browse rows for the winning ids in one batch and keeps the ones that are still
    /// Active, in the order given. The status check is the guarantee that nothing sold or ended is shown.
    /// </summary>
    private async Task<List<ListingSummary>> ReadLiveSummariesAsync(
        IEnumerable<string> ids, Dictionary<string, string> slugById, CancellationToken ct)
    {
        var ordered = ids.ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var keys = ordered.Select(id => new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BrowseSk()),
        }).ToList();

        var byId = new Dictionary<string, Dictionary<string, AttributeValue>>(StringComparer.Ordinal);
        var pending = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
        {
            [options.TableName] = new KeysAndAttributes { Keys = keys },
        };

        for (var round = 0; round < 3 && pending.Count > 0; round++)
        {
            var response = await dynamo.BatchGetItemAsync(
                new BatchGetItemRequest { RequestItems = pending }, ct).ConfigureAwait(false);

            foreach (var row in response.Responses.GetValueOrDefault(options.TableName) ?? [])
            {
                byId[row.GetString(BrowseProjectionItem.IdAttr)] = row;
            }

            pending = response.UnprocessedKeys is { Count: > 0 } left ? left : [];
        }

        var summaries = new List<ListingSummary>(ordered.Count);
        foreach (var id in ordered)
        {
            if (byId.TryGetValue(id, out var row) && IsActive(row))
            {
                summaries.Add(ToPublicSummary(row, slugById));
            }
        }

        return summaries;
    }

    private async Task<List<ListingSummary>> NewestLiveAsync(
        string categoryId, Dictionary<string, string> slugById, CancellationToken ct)
    {
        var response = await dynamo.QueryAsync(
            new QueryRequest
            {
                TableName = options.TableName,
                IndexName = DynamoTable.BrowseNewestIndex,
                KeyConditionExpression = "#pk = :pk",
                FilterExpression = "#status = :active",
                ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["#pk"] = DynamoTable.BrowseNewestPk,
                    ["#status"] = BrowseProjectionItem.StatusAttr,
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [":pk"] = Attr.S(DynamoTable.BrowsePkValue(categoryId)),
                    [":active"] = Attr.S(nameof(ListingStatus.Active)),
                },
                ScanIndexForward = false,
                Limit = CategoryTopUpRead,
            },
            ct).ConfigureAwait(false);

        return [.. (response.Items ?? []).Select(row => ToPublicSummary(row, slugById))];
    }

    private static bool IsActive(Dictionary<string, AttributeValue> row) =>
        string.Equals(row.GetStringOrNull(BrowseProjectionItem.StatusAttr), nameof(ListingStatus.Active), StringComparison.Ordinal);

    private static ListingSummary ToPublicSummary(Dictionary<string, AttributeValue> row, Dictionary<string, string> slugById) =>
        BrowseProjectionItem.ToSummary(
            row, slugById.GetValueOrDefault(row.GetString(BrowseProjectionItem.CategoryIdAttr), string.Empty)) with
        {
            // The browse row carries the sign-in name, which can be an email address. A suggestion
            // never needs a seller, so none of it leaves this method.
            SellerUsername = string.Empty,
            SellerId = null,
        };

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Suggestion rows for listing {ListingId} could not be written; the next edit or the backfill repairs them.")]
    private static partial void LogSuggestionRowsFailed(ILogger logger, string listingId, Exception failure);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Suggestion rows for listing {ListingId} were only partly written after retries.")]
    private static partial void LogSuggestionRowsIncomplete(ILogger logger, string listingId);
}
