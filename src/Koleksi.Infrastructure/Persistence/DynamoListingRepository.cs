using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Catalog;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Stores listings in the single table.
/// </summary>
/// <remarks>
/// Writes are conditional. An insert asserts the item does not exist, an update asserts
/// the version is unchanged. Neither is a courtesy: without the first, a retried create
/// silently overwrites a listing that already sold, and without the second, two tabs open
/// on the same listing quietly discard one seller's edit.
/// </remarks>
internal sealed class DynamoListingRepository(IAmazonDynamoDB dynamo, DynamoOptions options)
    : IListingRepository
{
    public async Task<Listing?> FindAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = KeyOf(listingId),

                // Strongly consistent: a seller who has just saved an edit and been
                // redirected to their listing must not be shown the pre-edit copy.
                ConsistentRead = true,
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item ? ListingItem.ToDomain(item) : null;
    }

    public async Task AddAsync(Listing listing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);

        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = ListingItem.ToItem(listing),

                    // Ids are UUIDv7, so a collision means a bug rather than bad luck —
                    // but an unconditional put would also let a replayed create request
                    // reset a live listing back to its draft state.
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException($"Listing '{listing.Id}' already exists.");
        }
    }

    /// <summary>
    /// Saves a listing, failing if it changed since it was read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An update, not a put. A put replaces the whole item, and <c>viewCount</c> and
    /// <c>watcherCount</c> are deliberately absent from the mapped item because they are
    /// maintained by atomic adds outside this path — so a put would delete them, and every
    /// seller edit would silently reset the listing's view count to nothing.
    /// </para>
    /// <para>
    /// Writing them from the aggregate instead would be no better: the copy in hand was
    /// read when the seller opened the form, so saving it would roll back every view
    /// counted while they typed. Assigning only the attributes this layer owns is the only
    /// version that is right in both directions.
    /// </para>
    /// <para>
    /// The auction and browse rows keep using a put on purpose. They have no externally
    /// maintained attributes, and both rely on a put's replace semantics to <i>drop</i>
    /// sparse index keys — which is how a closed auction leaves the close sweep's index.
    /// </para>
    /// </remarks>
    public async Task UpdateAsync(Listing listing, long expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var item = ListingItem.ToItem(listing);

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#v"] = ListingItem.VersionAttribute,
        };

        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":expected"] = Attr.N(expectedVersion),
        };

        var assignments = new List<string>(item.Count);
        var index = 0;

        foreach (var (attribute, value) in item)
        {
            // The key identifies the row; DynamoDB rejects an update that assigns to it.
            if (string.Equals(attribute, DynamoTable.Pk, StringComparison.Ordinal)
                || string.Equals(attribute, DynamoTable.Sk, StringComparison.Ordinal))
            {
                continue;
            }

            var nameToken = $"#a{index.ToString(CultureInfo.InvariantCulture)}";
            var valueToken = $":a{index.ToString(CultureInfo.InvariantCulture)}";

            names[nameToken] = attribute;
            values[valueToken] = value;
            assignments.Add($"{nameToken} = {valueToken}");
            index++;
        }

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = KeyOf(listing.Id),
                    UpdateExpression = "SET " + string.Join(", ", assignments),

                    // Existence is asserted as well as the version. Without it an update of
                    // a listing that was never created would quietly create one, bypassing
                    // every check AddAsync makes.
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk}) AND #v = :expected",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            throw new ConcurrencyConflictException(
                $"Listing '{listing.Id}' was modified by another request, or does not exist.");
        }
    }

    /// <summary>
    /// The most queries one <see cref="ListBySellerAsync"/> call makes while looking past rows
    /// that are not listings. Generous for a normal partition and a hard stop for a pathological
    /// one; a call that stops here returns a short page with a cursor, so nothing is lost.
    /// </summary>
    private const int MaxSellerQueryRounds = 10;

    public async Task<Page<Listing>> ListBySellerAsync(
        string sellerId, ListingStatus? status, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var values = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [":pk"] = Attr.S(DynamoTable.SellerPkValue(sellerId)),
        };

        var keyCondition = $"{DynamoTable.SellerPk} = :pk";
        if (status is { } wanted)
        {
            // Status leads the sort key, so filtering by it is a key condition. As a filter
            // expression it would still be correct but would read — and bill for — every
            // listing the seller has ever created.
            keyCondition += $" AND begins_with({DynamoTable.SellerSk}, :skPrefix)";
            values[":skPrefix"] = Attr.S(DynamoTable.SellerSkPrefix(wanted.ToString()));
        }

        // The seller index is shared: listings, received offers, orders and conversations all
        // carry gsi_seller_* keys, told apart only by the sort key's prefix. With no status to
        // narrow by there is no key condition that selects listings alone (their statuses sort
        // on either side of CONV#, OFFER# and ORDER#), so the rows are filtered by type instead.
        // Without this, the first non-listing row in a seller's partition made ToDomain throw
        // and took their whole dashboard down.
        values[":listing"] = Attr.S(ListingItem.TypeName);

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#entity"] = DynamoTable.EntityType,
        };

        var listings = new List<Listing>();
        var startKey = DynamoCursor.Decode(cursor);
        var rounds = 0;

        // A filter runs after Limit, so one query can come back short — or empty — while more
        // listings lie further on. Keep reading until the page is full or the partition is
        // exhausted. Each round asks only for what is still missing, so the key a round ends
        // on is exactly where the page ends and is a correct cursor to resume from.
        do
        {
            var response = await dynamo.QueryPageAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.SellerIndex,
                    KeyConditionExpression = keyCondition,
                    FilterExpression = "#entity = :listing",
                    ExpressionAttributeNames = names,
                    ExpressionAttributeValues = values,
                    ExclusiveStartKey = startKey,
                    Limit = limit - listings.Count,

                    // Newest first: ids are time-ordered, so descending on the sort key is
                    // descending by creation time within a status.
                    ScanIndexForward = false,
                },
                ct).ConfigureAwait(false);

            foreach (var item in response.Items ?? [])
            {
                listings.Add(ListingItem.ToDomain(item));
            }

            startKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
            rounds++;
        }
        while (listings.Count < limit && startKey is not null && rounds < MaxSellerQueryRounds);

        return new Page<Listing>(
            listings,
            DynamoCursor.Encode(startKey),

            // The dashboard shows "12 active" from a maintained counter, not from a count
            // of the page in hand. Reporting the page length here would render "12 of 12"
            // for a seller with four hundred listings.
            TotalCount: listings.Count);
    }

    public async Task IncrementViewCountAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = KeyOf(listingId),

                    // ADD, not a read-modify-write. Two simultaneous page views must both
                    // count, and neither may collide with a seller's concurrent edit.
                    UpdateExpression = "ADD #views :one",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#views"] = ListingItem.ViewCountAttribute,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":one"] = Attr.N(1L),
                    },

                    // Do not conjure an item for a listing id that does not exist. Without
                    // this, requesting a made-up id writes a partial row on every hit —
                    // an unauthenticated way to fill the table.
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // The listing is gone. A view of something that no longer exists is not an
            // error worth surfacing to the reader.
        }
        catch (TransactionConflictException)
        {
            // Someone saved or unsaved this listing in the very instant of the view (the
            // watchlist moves the watcher count in a transaction on this same row, and
            // DynamoDB turns a plain write away while one is in flight). A lost view is not
            // worth a failed page load, and the doc above already says it is not worth a retry.
        }
    }

    /// <summary>
    /// Reads the seller's handle back off the browse projection row, which shares this
    /// listing's partition.
    /// </summary>
    /// <remarks>
    /// Written by <see cref="DynamoListingSearchIndex.IndexAsync"/>, not by this
    /// repository — but the key is built centrally in <see cref="DynamoTable"/> either way,
    /// so reading a row this type does not itself write is still a same-partition
    /// <c>GetItem</c> rather than a second subsystem.
    /// </remarks>
    public async Task<string?> FindSellerUsernameAsync(string listingId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listingId);

        var response = await dynamo.GetItemAsync(
            new GetItemRequest
            {
                TableName = options.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.BrowseSk()),
                },
            },
            ct).ConfigureAwait(false);

        return response.Item is { Count: > 0 } item
            ? item.GetStringOrNull(BrowseProjectionItem.SellerUsernameAttr)
            : null;
    }

    /// <summary>Most pages a count will walk; at 1 MB a page this is far beyond any seller's real catalogue.</summary>
    private const int MaxCountRounds = 20;

    public async Task<int> CountBySellerAsync(string sellerId, ListingStatus status, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        // Status leads the sort key, so this is a key-condition prefix: it reads only index keys
        // for that status and never a listing body. Offers, orders and conversations share the
        // partition but sort under other prefixes, so none of them is counted.
        var total = 0;
        Dictionary<string, AttributeValue>? startKey = null;

        for (var round = 0; round < MaxCountRounds; round++)
        {
            var response = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    IndexName = DynamoTable.SellerIndex,
                    KeyConditionExpression = $"{DynamoTable.SellerPk} = :pk AND begins_with({DynamoTable.SellerSk}, :prefix)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.SellerPkValue(sellerId)),
                        [":prefix"] = Attr.S(DynamoTable.SellerSkPrefix(status.ToString())),
                    },
                    Select = Select.COUNT,
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            total += response.Count ?? 0;
            startKey = response.LastEvaluatedKey is { Count: > 0 } ? response.LastEvaluatedKey : null;
            if (startKey is null)
            {
                break;
            }
        }

        return total;
    }

    public async Task<IReadOnlyDictionary<string, string>> FindSellerIdsAsync(
        IReadOnlyCollection<string> listingIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listingIds);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var distinct = listingIds.Distinct(StringComparer.Ordinal).ToList();

        // BatchGetItem takes at most 100 keys; a browse page is at most 48.
        for (var offset = 0; offset < distinct.Count; offset += 100)
        {
            var pending = new Dictionary<string, KeysAndAttributes>(StringComparer.Ordinal)
            {
                [options.TableName] = new KeysAndAttributes
                {
                    Keys = [.. distinct.Skip(offset).Take(100).Select(KeyOf)],
                    ProjectionExpression = "#id, #seller",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#id"] = "id",
                        ["#seller"] = "sellerId",
                    },
                },
            };

            for (var round = 0; round < 4 && pending.Count > 0; round++)
            {
                var response = await dynamo.BatchGetItemAsync(
                    new BatchGetItemRequest { RequestItems = pending }, ct).ConfigureAwait(false);

                foreach (var item in response.Responses.GetValueOrDefault(options.TableName) ?? [])
                {
                    found[item.GetString("id")] = item.GetString("sellerId");
                }

                pending = response.UnprocessedKeys is { Count: > 0 } ? response.UnprocessedKeys : [];
            }
        }

        return found;
    }

    private static Dictionary<string, AttributeValue> KeyOf(string listingId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listingId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ListingMetaSk()),
        };
}

/// <summary>
/// Encodes a DynamoDB <c>LastEvaluatedKey</c> as an opaque page cursor.
/// </summary>
/// <remarks>
/// <para>
/// The cursor is base64url over a compact text form. It is opaque but <b>not</b> a
/// security boundary: a caller can decode it, and could hand back a forged one. That is
/// tolerable only because every query that accepts a cursor also constrains its partition
/// key from the caller's own identity or from a public category — so the worst a forged
/// cursor can do is start the caller's own result set in an odd place.
/// </para>
/// <para>
/// Do not extend this to queries whose partition is taken from the cursor itself. That
/// would turn a forged cursor into a read of someone else's partition.
/// </para>
/// </remarks>
internal static class DynamoCursor
{
    // ASCII unit and record separators, written numerically on purpose: as literal control
    // characters they are invisible in an editor and in a diff. Chosen because they cannot
    // occur in a DynamoDB key attribute, so no key value can split a cursor into the wrong
    // number of fields.
    private const char PairSeparator = (char)0x1F;
    private const char FieldSeparator = (char)0x1E;

    /// <summary>
    /// Runs a query that may resume from a caller-supplied cursor. DynamoDB refuses a start key that does not fit
    /// the query ("starting key is invalid", "does not match the range key predicate", "outside query
    /// boundaries"), which is also why a forged cursor can never reach another partition. That refusal is the
    /// caller's mistake, so it becomes <see cref="InvalidCursorException"/> (a 400) instead of an unhandled 500.
    /// </summary>
    public static async Task<QueryResponse> QueryPageAsync(
        this IAmazonDynamoDB dynamo, QueryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dynamo);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return await dynamo.QueryAsync(request, ct).ConfigureAwait(false);
        }
        catch (AmazonDynamoDBException failure)
            when (request.ExclusiveStartKey is { Count: > 0 }
                && IsBadStartKey(failure))
        {
            throw new InvalidCursorException("The page cursor is not valid.", failure);
        }
    }

    // DynamoDB words this several ways ("The provided starting key is invalid", "...does not match the range key
    // predicate", "Exclusive Start Key must have same size as table's key schema"); all of them name the start key.
    private static bool IsBadStartKey(AmazonDynamoDBException failure) =>
        failure.Message.Contains("starting key", StringComparison.OrdinalIgnoreCase)
        || failure.Message.Contains("start key", StringComparison.OrdinalIgnoreCase);

    public static string? Encode(Dictionary<string, AttributeValue>? lastEvaluatedKey)
    {
        if (lastEvaluatedKey is not { Count: > 0 })
        {
            return null;
        }

        var parts = new List<string>(lastEvaluatedKey.Count);
        foreach (var (name, value) in lastEvaluatedKey)
        {
            // Only the scalar key types DynamoDB permits in a key attribute.
            var encoded = value switch
            {
                { S: { } text } => $"S{FieldSeparator}{name}{FieldSeparator}{text}",
                { N: { } number } => $"N{FieldSeparator}{name}{FieldSeparator}{number}",
                _ => null,
            };

            if (encoded is not null)
            {
                parts.Add(encoded);
            }
        }

        var payload = string.Join(PairSeparator, parts);
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    public static Dictionary<string, AttributeValue>? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            // A malformed cursor is a client error, not a server fault. Starting from the
            // first page is the behaviour a user would expect from a mangled URL.
            return null;
        }

        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal);
        foreach (var part in payload.Split(PairSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split(FieldSeparator);
            if (fields.Length != 3)
            {
                return null;
            }

            key[fields[1]] = fields[0] switch
            {
                "S" => Attr.S(fields[2]),
                "N" when long.TryParse(fields[2], CultureInfo.InvariantCulture, out var parsed) => Attr.N(parsed),
                _ => new AttributeValue(),
            };
        }

        return key.Count > 0 ? key : null;
    }
}
