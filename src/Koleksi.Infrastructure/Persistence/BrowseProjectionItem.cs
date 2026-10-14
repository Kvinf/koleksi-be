using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// The browse projection: one row per publicly visible listing, holding exactly what a
/// result card draws and what a filter matches on.
/// </summary>
/// <remarks>
/// <para>
/// Narrower than the listing itself, and that narrowness is a control rather than an
/// optimisation. Browse is served to anonymous visitors; a field added to
/// <see cref="Listing"/> cannot start leaking into public results unless someone
/// deliberately adds it here too.
/// </para>
/// <para>
/// Facet values are flattened into <c>f_{key}</c> attributes so a filter expression can
/// match them without loading a nested map. Keys come from the category schema, which the
/// domain validates against an allowlist, so a seller cannot invent an attribute name and
/// mint a new indexed column.
/// </para>
/// </remarks>
internal static class BrowseProjectionItem
{
    public const string TypeName = "Browse";

    public const string IdAttr = "id";
    public const string TitleAttr = "title";
    public const string CategoryIdAttr = "categoryId";
    public const string PriceAttr = "price";
    public const string FormatAttr = "format";
    public const string StatusAttr = "status";
    public const string ConditionAttr = "condition";
    public const string SellerUsernameAttr = "sellerUsername";

    /// <summary>
    /// The owning account, so the seller's public handle can be resolved at read time. Absent on
    /// rows indexed before it existed; the browse handler looks those up from the listing.
    /// </summary>
    public const string SellerIdAttr = "sellerId";
    public const string CityAttr = "city";
    public const string PrimaryPhotoAttr = "primaryPhoto";
    public const string AuctionEndsAtAttr = "auctionEndsAt";
    public const string BidCountAttr = "bidCount";

    /// <summary>
    /// A lowercased copy of the title, for the free-text fallback.
    /// </summary>
    /// <remarks>
    /// DynamoDB's <c>contains</c> is case-sensitive and has no notion of tokens or stems,
    /// so this is a substring match over a normalised copy — not search. It is honest
    /// enough for a launch catalogue and is the first thing OpenSearch replaces.
    /// </remarks>
    public const string SearchTextAttr = "searchText";

    /// <summary>Prefix for flattened facet values, e.g. <c>f_grade</c>.</summary>
    public const string FacetPrefix = "f_";

    public static string FacetAttr(string facetKey) => FacetPrefix + facetKey;

    public static Dictionary<string, AttributeValue> ToItem(
        Listing listing, string sellerUsername, DateTimeOffset? auctionEndsAt, int bidCount)
    {
        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listing.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.BrowseSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(listing.Id),
            [TitleAttr] = Attr.S(listing.Title),
            [CategoryIdAttr] = Attr.S(listing.CategoryId),
            [PriceAttr] = Attr.MoneyValue(listing.Price),
            [FormatAttr] = Attr.EnumValue(listing.Format),
            [StatusAttr] = Attr.EnumValue(listing.Status),
            [ConditionAttr] = Attr.S(listing.Condition),
            [SellerUsernameAttr] = Attr.S(sellerUsername),
            [SellerIdAttr] = Attr.S(listing.SellerId),
            [CityAttr] = Attr.S(listing.City),
            [PrimaryPhotoAttr] = Attr.SOrNull(
                listing.Photos.Count > 0 ? listing.Photos[0].StorageKey : null),
            [AuctionEndsAtAttr] = Attr.InstantOrNull(auctionEndsAt),
            [BidCountAttr] = Attr.N(bidCount),
            [SearchTextAttr] = Attr.S(listing.Title.ToLowerInvariant()),

            [DynamoTable.BrowseNewestPk] = Attr.S(DynamoTable.BrowsePkValue(listing.CategoryId)),
            [DynamoTable.BrowseNewestSk] = Attr.S(DynamoTable.BrowseNewestSkValue(listing.Id)),
            [DynamoTable.BrowsePricePk] = Attr.S(DynamoTable.BrowsePkValue(listing.CategoryId)),
            [DynamoTable.BrowsePriceSk] = Attr.S(
                DynamoTable.BrowsePriceSkValue(listing.Price.Rupiah, listing.Id)),
        };

        foreach (var (key, value) in listing.Attributes)
        {
            item[FacetAttr(key)] = Attr.S(value);
        }

        // Sparse: only a live auction appears in the "ending soon" index, so a fixed-price
        // listing costs nothing there and a closed auction drops out on its next write.
        if (auctionEndsAt is { } endsAt && listing.Format == ListingFormat.Auction)
        {
            item[DynamoTable.BrowseEndingPk] = Attr.S(DynamoTable.BrowseEndingPkValue(listing.CategoryId));
            item[DynamoTable.BrowseEndingSk] = Attr.S(
                DynamoTable.AuctionEndSkValue(endsAt, listing.Id));
        }

        return item;
    }

    public static ListingSummary ToSummary(
        Dictionary<string, AttributeValue> item, string categorySlug) => new(
        Id: item.GetString(IdAttr),
        Title: item.GetString(TitleAttr),
        CategoryId: item.GetString(CategoryIdAttr),
        CategorySlug: categorySlug,
        PriceRupiah: item.GetLong(PriceAttr),
        Format: item.GetEnum<ListingFormat>(FormatAttr),
        Status: item.GetEnum<ListingStatus>(StatusAttr),
        Condition: item.GetString(ConditionAttr),
        SellerUsername: item.GetString(SellerUsernameAttr),
        City: item.GetString(CityAttr),
        PrimaryPhotoKey: item.GetStringOrNull(PrimaryPhotoAttr),
        AuctionEndsAt: item.GetInstantOrNull(AuctionEndsAtAttr),
        BidCount: (int)item.GetLongOrDefault(BidCountAttr),
        SellerId: item.GetStringOrNull(SellerIdAttr));
}
