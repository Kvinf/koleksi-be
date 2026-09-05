using System.Globalization;
using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// The single-table layout: every key, index, and attribute name in one place.
/// </summary>
/// <remarks>
/// <para>
/// One table holds every aggregate. The alternative — a table per aggregate — costs a
/// second round trip wherever two aggregates are written together, and DynamoDB
/// transactions are cheaper and simpler within one table.
/// </para>
/// <para>
/// Keys are built here and nowhere else. A repository that composes its own key string
/// is one typo away from writing an item nothing can ever read back, and that typo is
/// invisible in review. Every builder is a pure function with a test.
/// </para>
/// </remarks>
internal static class DynamoTable
{
    // ─── Key attributes ─────────────────────────────────────────────────────────
    public const string Pk = "pk";
    public const string Sk = "sk";

    // ─── Indexes ────────────────────────────────────────────────────────────────

    /// <summary>Seller's own listings, including drafts. Drives the dashboard.</summary>
    public const string SellerIndex = "gsi_seller";
    public const string SellerPk = "gsi_seller_pk";
    public const string SellerSk = "gsi_seller_sk";

    /// <summary>Browse by category, newest first.</summary>
    public const string BrowseNewestIndex = "gsi_browse_new";
    public const string BrowseNewestPk = "gsi_browse_new_pk";
    public const string BrowseNewestSk = "gsi_browse_new_sk";

    /// <summary>Browse by category, ordered by price.</summary>
    public const string BrowsePriceIndex = "gsi_browse_price";
    public const string BrowsePricePk = "gsi_browse_price_pk";
    public const string BrowsePriceSk = "gsi_browse_price_sk";

    /// <summary>
    /// Browse a category's live auctions by close time — the "ending soon" sort.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AuctionEndIndex"/> because the two answer different
    /// questions. This one is partitioned by category, for a shopper narrowing to cards
    /// ending today; that one is partitioned by shard, for a sweep that must see every
    /// auction in the marketplace regardless of category.
    /// </remarks>
    public const string BrowseEndingIndex = "gsi_browse_ending";
    public const string BrowseEndingPk = "gsi_browse_ending_pk";
    public const string BrowseEndingSk = "gsi_browse_ending_sk";

    /// <summary>
    /// Live auctions ordered by close time. Sparse on purpose — only live auctions carry
    /// these attributes, so the index holds a few hundred items rather than the whole
    /// catalogue, and both "ending soon" browse and the close sweep read it cheaply.
    /// </summary>
    public const string AuctionEndIndex = "gsi_auction_end";
    public const string AuctionEndPk = "gsi_auction_end_pk";
    public const string AuctionEndSk = "gsi_auction_end_sk";

    /// <summary>The buyer-side mirror of <see cref="SellerIndex"/>: offers sent, orders bought.</summary>
    public const string BuyerIndex = "gsi_buyer";
    public const string BuyerPk = "gsi_buyer_pk";
    public const string BuyerSk = "gsi_buyer_sk";

    // ─── Item type discriminator ────────────────────────────────────────────────
    public const string EntityType = "entity";

    // ─── Sort-key suffixes ──────────────────────────────────────────────────────
    private const string MetaSuffix = "#META";
    private const string AuctionSuffix = "#AUCTION";
    private const string BrowseSuffix = "#BROWSE";

    // ─── Partition prefixes ─────────────────────────────────────────────────────
    private const string ListingPrefix = "LISTING#";
    private const string SellerPrefix = "SELLER#";
    private const string BuyerPrefix = "BUYER#";
    private const string BidPrefix = "BID#";
    private const string OfferPrefix = "OFFER#";
    private const string OfferActiveSuffix = "OFFERACTIVE#";
    private const string OrderPrefix = "ORDER#";
    private const string OrderNumberPrefix = "ORDERNUM#";
    private const string BrowsePrefix = "BROWSE#";
    private const string FacetPrefix = "FACETS#";
    private const string IdempotencyPrefix = "IDEMPOTENCY#";
    private const string UserPrefix = "USER#";
    private const string PhoneOtpPrefix = "PHONEOTP#";
    private const string EmailOtpPrefix = "EMAILOTP#";
    private const string CartPrefix = "CART#";
    private const string CartItemPrefix = "ITEM#";

    /// <summary>
    /// Width used when encoding a price into a sort key.
    /// </summary>
    /// <remarks>
    /// DynamoDB sorts strings lexicographically, so a numeric sort key must be
    /// zero-padded to a fixed width or <c>Rp9.000</c> sorts after <c>Rp10.000.000</c>.
    /// Sixteen digits covers any rupiah amount that fits in a <see cref="long"/>.
    /// </remarks>
    private const int PriceKeyWidth = 16;

    // ─── Listing ────────────────────────────────────────────────────────────────

    /// <summary>Partition holding a listing and everything that hangs off it.</summary>
    /// <remarks>
    /// The listing, its auction, its bids, and its browse projection share a partition so
    /// a bid and the auction it changes can be written in one transaction, and so a
    /// listing and its projection can never drift into different shards.
    /// </remarks>
    public static string ListingPk(string listingId) => ListingPrefix + Require(listingId);

    public static string ListingMetaSk() => MetaSuffix;

    public static string AuctionSk() => AuctionSuffix;

    public static string BrowseSk() => BrowseSuffix;

    /// <summary>
    /// Sort key for one bid.
    /// </summary>
    /// <remarks>
    /// Bid ids are UUIDv7 rendered in Crockford base-32, which is ordered by time and
    /// fixed-width. Sorting on the id therefore sorts chronologically with no separate
    /// timestamp attribute, and no two bids in the same millisecond can collide.
    /// </remarks>
    public static string BidSk(string bidId) => BidPrefix + Require(bidId);

    /// <summary>Everything under one listing, for a single-partition read.</summary>
    public static string BidSkPrefix() => BidPrefix;

    // ─── Seller index ───────────────────────────────────────────────────────────

    public static string SellerPkValue(string sellerId) => SellerPrefix + Require(sellerId);

    /// <summary>
    /// Seller-index sort key: status first, then the time-ordered id.
    /// </summary>
    /// <remarks>
    /// Status leads so the dashboard's "Active" tab is a key-condition prefix query
    /// rather than a filtered scan of every listing the seller has ever created.
    /// </remarks>
    public static string SellerSkValue(string status, string listingId) =>
        $"{Require(status)}#{Require(listingId)}";

    public static string SellerSkPrefix(string status) => $"{Require(status)}#";

    /// <summary>
    /// A non-listing item's entry in the seller index, multiplexed by item-type prefix
    /// alongside <see cref="SellerSkValue(string, string)"/>'s status-prefixed listing
    /// rows. Time-ordered ids sort chronologically within the prefix, so newest-first is
    /// <c>ScanIndexForward: false</c> at read time, the same technique the bid history uses.
    /// </summary>
    public static string SellerSkValue(string typePrefix, string createdAt, string id) =>
        $"{Require(typePrefix)}{Require(createdAt)}#{Require(id)}";

    // ─── Buyer index ────────────────────────────────────────────────────────────

    public static string BuyerPkValue(string buyerId) => BuyerPrefix + Require(buyerId);

    /// <summary>
    /// A buyer-index sort key, multiplexed by item-type prefix the same way
    /// <see cref="SellerSkValue(string, string, string)"/> is on the seller side.
    /// </summary>
    public static string BuyerSkValue(string typePrefix, string createdAt, string id) =>
        $"{Require(typePrefix)}{Require(createdAt)}#{Require(id)}";

    // ─── Offer ──────────────────────────────────────────────────────────────────

    public static string OfferPk(string offerId) => OfferPrefix + Require(offerId);

    public static string OfferMetaSk() => MetaSuffix;

    /// <summary>Type-prefix for an offer's row in <see cref="SellerIndex"/>/<see cref="BuyerIndex"/>.</summary>
    public static string OfferIndexPrefix() => OfferPrefix;

    /// <summary>
    /// Pointer, in the listing's own partition, to a buyer's currently-unresolved offer.
    /// </summary>
    /// <remarks>
    /// The primary offer row is keyed by offer id alone (<see cref="OfferPk"/>), because
    /// <c>IOfferRepository.FindAsync</c> only ever has an offer id to look up by. That
    /// leaves no cheap way to answer "does this buyer already have an open offer on this
    /// listing" without this second, small pointer row, written and cleared alongside the
    /// main one in the same transaction.
    /// </remarks>
    public static string OfferActiveSk(string buyerId) => OfferActiveSuffix + Require(buyerId);

    // ─── Order ──────────────────────────────────────────────────────────────────

    public static string OrderPk(string orderId) => OrderPrefix + Require(orderId);

    public static string OrderMetaSk() => MetaSuffix;

    /// <summary>Type-prefix for an order's row in <see cref="SellerIndex"/>/<see cref="BuyerIndex"/>.</summary>
    public static string OrderIndexPrefix() => OrderPrefix;

    /// <summary>
    /// A standalone pointer item resolving a human-facing order number back to its id.
    /// </summary>
    /// <remarks>
    /// The number is the last six characters of the id (<c>Order.BuildOrderNumber</c>),
    /// not a reversible encoding of the whole thing, so the payment webhook — which only
    /// ever receives the number — needs a real lookup, not arithmetic on the string.
    /// </remarks>
    public static string OrderNumberPk(string orderNumber) => OrderNumberPrefix + Require(orderNumber);

    public static string OrderNumberSk() => MetaSuffix;

    // ─── Browse indexes ─────────────────────────────────────────────────────────

    /// <summary>
    /// Browse partition for one category.
    /// </summary>
    /// <remarks>
    /// Deliberately per-category rather than one <c>ALL</c> partition. A single partition
    /// for the whole catalogue would be a write hotspot and would cap browse throughput
    /// at one partition's limits. Cross-category search fans out across the six category
    /// partitions in parallel instead.
    /// </remarks>
    public static string BrowsePkValue(string categoryId) => BrowsePrefix + Require(categoryId);

    /// <summary>Newest-first sort key. The bare listing id, which is already time-ordered.</summary>
    public static string BrowseNewestSkValue(string listingId) => Require(listingId);

    /// <summary>Price sort key: zero-padded price, then id to break ties deterministically.</summary>
    public static string BrowsePriceSkValue(long priceRupiah, string listingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priceRupiah);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{priceRupiah.ToString(CultureInfo.InvariantCulture).PadLeft(PriceKeyWidth, '0')}#{Require(listingId)}");
    }

    /// <summary>Browse partition for a category's live auctions, ordered by close time.</summary>
    public static string BrowseEndingPkValue(string categoryId) => "BROWSE_END#" + Require(categoryId);

    /// <summary>
    /// How many partitions the live-auction index is spread across.
    /// </summary>
    /// <remarks>
    /// Every bid rewrites its auction item and therefore its entry in this index. A single
    /// partition would funnel every bid in the marketplace through one partition's write
    /// limit, and the last minute of a popular lot is exactly when that limit matters.
    /// Four shards is a deliberate floor: the close sweep queries all of them in parallel,
    /// so the constant is a fan-out cost as well as a throughput gain.
    /// </remarks>
    public const int AuctionShardCount = 4;

    /// <summary>Live-auction partition for one shard.</summary>
    public static string AuctionEndPkValue(string status, int shard) =>
        $"AUCTION#{Require(status)}#{shard.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Picks a listing's shard deterministically.
    /// </summary>
    /// <remarks>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>, which is randomised per
    /// process. With the runtime's hash, two Lambda instances would place the same listing
    /// in different shards: the update from the second instance would write a fresh index
    /// entry and leave the first one behind, so the auction would appear twice in the close
    /// sweep and once at a stale close time. Any hash used in a persisted key must be
    /// stable across processes and across deployments.
    /// </remarks>
    public static int ShardOf(string listingId)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        var hash = OffsetBasis;
        foreach (var c in Require(listingId))
        {
            hash = (hash ^ c) * Prime;
        }

        return (int)(hash % AuctionShardCount);
    }

    /// <summary>Close-time sort key. Round-trip ISO-8601 in UTC sorts lexicographically by instant.</summary>
    public static string AuctionEndSkValue(DateTimeOffset endsAt, string listingId) =>
        $"{FormatInstant(endsAt)}#{Require(listingId)}";

    /// <summary>
    /// An inclusive upper bound on <see cref="AuctionEndSkValue"/> for a given instant.
    /// </summary>
    /// <remarks>
    /// The sort key appends an id after the instant, so <c>sk &lt;= "{instant}"</c> excludes
    /// every auction closing at exactly that instant. <c>U+FFFF</c> encodes to bytes above
    /// anything the Crockford base-32 alphabet produces, so it bounds the whole tick.
    /// </remarks>
    public static string EndsAtUpperBound(DateTimeOffset asOf) =>
        $"{FormatInstant(asOf)}#{char.MaxValue}";

    // ─── Facet counters ─────────────────────────────────────────────────────────

    public static string FacetPkValue(string categoryId) => FacetPrefix + Require(categoryId);

    public static string FacetSkValue(string facetKey, string value) =>
        $"{Require(facetKey)}#{Require(value)}";

    // ─── Idempotency ────────────────────────────────────────────────────────────

    /// <summary>
    /// Idempotency keys are scoped per caller.
    /// </summary>
    /// <remarks>
    /// The scope is part of the partition key, so one caller's key can neither collide
    /// with nor be probed for by another's. A global key namespace would let anyone
    /// suppress someone else's checkout by guessing their key.
    /// </remarks>
    public static string IdempotencyPkValue(string scope, string key) =>
        $"{IdempotencyPrefix}{Require(scope)}#{Require(key)}";

    // ─── Profile ────────────────────────────────────────────────────────────────

    /// <summary>Partition for one account's profile — keyed by Cognito subject, not a minted id.</summary>
    public static string UserPk(string userId) => UserPrefix + Require(userId);

    public static string ProfileSk() => "#PROFILE";

    // ─── Phone OTP ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Partition for one phone number's outstanding verification challenge.
    /// </summary>
    /// <remarks>
    /// Keyed by the phone number rather than the account: the number is what a code is
    /// sent to, and the challenge has to be findable by that number alone at verify time
    /// before it is known to belong to any particular account's profile.
    /// </remarks>
    public static string PhoneOtpPkValue(string normalizedPhone) => PhoneOtpPrefix + Require(normalizedPhone);

    public static string PhoneOtpSk() => "#CHALLENGE";

    // ─── Email OTP ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Partition for one email address's outstanding verification challenge.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="PhoneOtpPkValue"/>: keyed by the address the code was
    /// sent to, findable at verify time before it is known to belong to any account.
    /// Normalised to lowercase, unlike the phone number, because email addresses are
    /// case-insensitive by convention and a reader who typed their address in Title Case
    /// once must still be able to verify it.
    /// </remarks>
    public static string EmailOtpPkValue(string normalizedEmail) => EmailOtpPrefix + Require(normalizedEmail);

    public static string EmailOtpSk() => "#CHALLENGE";

    // ─── Cart ───────────────────────────────────────────────────────────────────

    /// <summary>Partition holding one buyer's whole cart — one row per listing added.</summary>
    public static string CartPk(string buyerId) => CartPrefix + Require(buyerId);

    public static string CartItemSk(string listingId) => CartItemPrefix + Require(listingId);

    public static string CartItemSkPrefix() => CartItemPrefix;

    // ─── Encoding ───────────────────────────────────────────────────────────────

    /// <summary>Round-trip UTC ISO-8601. Sorts lexicographically in instant order.</summary>
    public static string FormatInstant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseInstant(string value) =>
        DateTimeOffset.ParseExact(
            value, "yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal);

    /// <summary>Unix seconds, the only form DynamoDB's TTL feature accepts.</summary>
    public static long ToTtl(DateTimeOffset expiresAt) => expiresAt.ToUnixTimeSeconds();

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Key components must be non-empty.", nameof(value))
            : value;
}

/// <summary>
/// Conversions between domain values and <see cref="AttributeValue"/>.
/// </summary>
/// <remarks>
/// Hand-written rather than reflection-based (<c>DynamoDBContext</c>). The domain
/// aggregates have private setters and no parameterless constructors by design, so a
/// mapper that sets properties reflectively would be working around the invariants the
/// domain exists to hold. Explicit mapping also survives trimming and native AOT, and it
/// makes the stored shape visible in review.
/// </remarks>
internal static class Attr
{
    public static AttributeValue S(string value) => new() { S = value };

    /// <summary>
    /// A string attribute, or a NULL attribute when the value is absent.
    /// </summary>
    /// <remarks>
    /// DynamoDB rejects an empty string in a key attribute and treats it as a real value
    /// elsewhere, so "absent" must be encoded explicitly rather than as "".
    /// </remarks>
    public static AttributeValue SOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? new AttributeValue { NULL = true } : new AttributeValue { S = value };

    public static AttributeValue N(long value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    public static AttributeValue N(int value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    public static AttributeValue Dec(decimal value) =>
        new() { N = value.ToString(CultureInfo.InvariantCulture) };

    public static AttributeValue Bool(bool value) => new() { BOOL = value };

    // Named with a "Value" suffix rather than after the type they carry: a static method
    // whose name matches its own parameter's type name shadows that type inside the
    // declaring class, and the resulting error points at the parameter, not the cause.
    public static AttributeValue MoneyValue(Money value) => N(value.Rupiah);

    public static AttributeValue MoneyValueOrNull(Money? value) =>
        value is { } money ? MoneyValue(money) : new AttributeValue { NULL = true };

    public static AttributeValue Instant(DateTimeOffset value) => S(DynamoTable.FormatInstant(value));

    public static AttributeValue InstantOrNull(DateTimeOffset? value) =>
        value is { } instant ? Instant(instant) : new AttributeValue { NULL = true };

    /// <summary>Stores an enum by name. Ordinals would silently change meaning on reorder.</summary>
    public static AttributeValue EnumValue<TEnum>(TEnum value) where TEnum : struct, Enum =>
        S(value.ToString()!);

    public static AttributeValue MapValue(Dictionary<string, AttributeValue> value) => new() { M = value };

    public static AttributeValue ListValue(List<AttributeValue> value) => new() { L = value };

    public static AttributeValue StringMap(IReadOnlyDictionary<string, string> value)
    {
        var map = new Dictionary<string, AttributeValue>(value.Count, StringComparer.Ordinal);
        foreach (var (key, item) in value)
        {
            map[key] = S(item);
        }

        return new AttributeValue { M = map };
    }
}

/// <summary>
/// Typed reads from a DynamoDB item.
/// </summary>
/// <remarks>
/// Every accessor states whether the attribute is required. A required attribute that is
/// missing throws with the attribute's name rather than yielding a default, because a
/// listing that silently rehydrates with a price of zero is far worse than one that fails
/// to load loudly.
/// </remarks>
internal static class DynamoItem
{
    public static string GetString(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.S is { } text
            ? text
            : throw new InvalidOperationException($"Item is missing required string attribute '{name}'.");

    public static string? GetStringOrNull(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.NULL != true ? value.S : null;

    public static long GetLong(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.N is { } number
            ? long.Parse(number, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"Item is missing required number attribute '{name}'.");

    public static long GetLongOrDefault(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.N is { } number
            ? long.Parse(number, CultureInfo.InvariantCulture)
            : 0L;

    public static int GetInt(this Dictionary<string, AttributeValue> item, string name) =>
        checked((int)item.GetLong(name));

    public static decimal? GetDecimalOrNull(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.NULL != true && value.N is { } number
            ? decimal.Parse(number, CultureInfo.InvariantCulture)
            : null;

    public static bool GetBool(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.BOOL == true;

    public static Money GetMoney(this Dictionary<string, AttributeValue> item, string name) =>
        Koleksi.Domain.Common.Money.FromRupiah(item.GetLong(name));

    public static Money? GetMoneyOrNull(this Dictionary<string, AttributeValue> item, string name) =>
        item.GetDecimalOrNull(name) is { } value ? Koleksi.Domain.Common.Money.FromRupiah((long)value) : null;

    public static DateTimeOffset GetInstant(this Dictionary<string, AttributeValue> item, string name) =>
        DynamoTable.ParseInstant(item.GetString(name));

    public static DateTimeOffset? GetInstantOrNull(this Dictionary<string, AttributeValue> item, string name) =>
        item.GetStringOrNull(name) is { } text ? DynamoTable.ParseInstant(text) : null;

    public static TEnum GetEnum<TEnum>(this Dictionary<string, AttributeValue> item, string name)
        where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(item.GetString(name), ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Attribute '{name}' holds a value that is not a member of {typeof(TEnum).Name}.");

    public static IReadOnlyDictionary<string, string> GetStringMap(
        this Dictionary<string, AttributeValue> item, string name)
    {
        if (!item.TryGetValue(name, out var value) || value.M is not { } map)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(map.Count, StringComparer.Ordinal);
        foreach (var (key, entry) in map)
        {
            if (entry.S is { } text)
            {
                result[key] = text;
            }
        }

        return result;
    }

    public static List<AttributeValue> GetList(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.L is { } list ? list : [];

    public static Dictionary<string, AttributeValue> GetMap(this Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.M is { } map
            ? map
            : throw new InvalidOperationException($"Item is missing required map attribute '{name}'.");
}
