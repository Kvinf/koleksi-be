using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Catalog;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// Maps a <see cref="Listing"/> to and from its stored item.
/// </summary>
/// <remarks>
/// Kept out of the repository so the shape of the row is one readable pair of functions.
/// Every attribute written here is read back in <see cref="ToDomain"/>; if you add one,
/// add it to both or the aggregate quietly loses the field on its next round trip.
/// </remarks>
internal static class ListingItem
{
    public const string TypeName = "Listing";

    private const string IdAttr = "id";
    private const string SellerIdAttr = "sellerId";
    private const string CategoryIdAttr = "categoryId";
    private const string TitleAttr = "title";
    private const string DescriptionAttr = "description";
    private const string PriceAttr = "price";
    private const string FormatAttr = "format";
    private const string StatusAttr = "status";
    private const string ConditionAttr = "condition";
    private const string CityAttr = "city";
    private const string PhotosAttr = "photos";
    private const string AttributesAttr = "attributes";
    private const string ViewCountAttr = "viewCount";
    private const string WatcherCountAttr = "watcherCount";
    private const string AutoDeclineAttr = "autoDeclineBelowPercent";
    private const string AuctionDurationAttr = "auctionDurationDays";
    private const string AuctionReserveAttr = "auctionReservePrice";
    private const string AuctionBuyoutAttr = "auctionBuyoutPrice";
    private const string StockAttr = "stock";
    private const string CreatedAtAttr = "createdAt";
    private const string UpdatedAtAttr = "updatedAt";
    private const string PublishedAtAttr = "publishedAt";
    private const string VersionAttr = "version";

    private const string PhotoKeyAttr = "k";
    private const string PhotoWidthAttr = "w";
    private const string PhotoHeightAttr = "h";

    /// <summary>The view counter, named here so the atomic-increment update can reference it.</summary>
    public static string ViewCountAttribute => ViewCountAttr;

    /// <summary>The watcher counter, named here so the watchlist's atomic add can reference it.</summary>
    public static string WatcherCountAttribute => WatcherCountAttr;

    /// <summary>The status, named here so the watchlist can condition a save on the listing being active.</summary>
    public static string StatusAttribute => StatusAttr;

    /// <summary>The concurrency token, named here so the conditional write can reference it.</summary>
    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> ToItem(Listing listing)
    {
        var photos = new List<AttributeValue>(listing.Photos.Count);
        foreach (var photo in listing.Photos)
        {
            photos.Add(Attr.MapValue(new Dictionary<string, AttributeValue>(3, StringComparer.Ordinal)
            {
                [PhotoKeyAttr] = Attr.S(photo.StorageKey),
                [PhotoWidthAttr] = Attr.N(photo.Width),
                [PhotoHeightAttr] = Attr.N(photo.Height),
            }));
        }

        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.ListingPk(listing.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ListingMetaSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(listing.Id),
            [SellerIdAttr] = Attr.S(listing.SellerId),
            [CategoryIdAttr] = Attr.S(listing.CategoryId),
            [TitleAttr] = Attr.S(listing.Title),
            [DescriptionAttr] = Attr.S(listing.Description),
            [PriceAttr] = Attr.MoneyValue(listing.Price),
            [FormatAttr] = Attr.EnumValue(listing.Format),
            [StatusAttr] = Attr.EnumValue(listing.Status),
            [ConditionAttr] = Attr.S(listing.Condition),
            [CityAttr] = Attr.S(listing.City),
            [PhotosAttr] = Attr.ListValue(photos),
            [AttributesAttr] = Attr.StringMap(listing.Attributes),
            [AutoDeclineAttr] = listing.AutoDeclineBelowPercent is { } percent
                ? Attr.Dec(percent)
                : new AttributeValue { NULL = true },
            [AuctionDurationAttr] = listing.AuctionDurationDays is { } days
                ? Attr.N(days)
                : new AttributeValue { NULL = true },
            [AuctionReserveAttr] = Attr.MoneyValueOrNull(listing.AuctionReservePrice),
            [AuctionBuyoutAttr] = Attr.MoneyValueOrNull(listing.AuctionBuyoutPrice),

            // Units on offer. Absent on rows written before stock existed, which read as one.
            [StockAttr] = Attr.N(listing.Stock),
            [CreatedAtAttr] = Attr.Instant(listing.CreatedAt),
            [UpdatedAtAttr] = Attr.Instant(listing.UpdatedAt),
            [PublishedAtAttr] = Attr.InstantOrNull(listing.PublishedAt),
            [VersionAttr] = Attr.N(listing.Version),

            // The seller index carries status in its sort key, so the dashboard's tabs are
            // prefix queries rather than filtered reads of a seller's whole history.
            [DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(listing.SellerId)),
            [DynamoTable.SellerSk] = Attr.S(
                DynamoTable.SellerSkValue(listing.Status.ToString(), listing.Id)),
        };

        // View and watcher counts are maintained by atomic ADD updates outside the
        // optimistic-concurrency path. Writing them here would let a stale in-memory copy
        // roll back counts that were incremented since this aggregate was read.
        return item;
    }

    public static Listing ToDomain(Dictionary<string, AttributeValue> item)
    {
        var photos = new List<ListingPhoto>();
        foreach (var entry in item.GetList(PhotosAttr))
        {
            if (entry.M is not { } map)
            {
                continue;
            }

            photos.Add(new ListingPhoto(
                map.GetString(PhotoKeyAttr),
                map.GetInt(PhotoWidthAttr),
                map.GetInt(PhotoHeightAttr)));
        }

        return Listing.Rehydrate(
            id: item.GetString(IdAttr),
            sellerId: item.GetString(SellerIdAttr),
            categoryId: item.GetString(CategoryIdAttr),
            title: item.GetString(TitleAttr),
            description: item.GetStringOrNull(DescriptionAttr) ?? string.Empty,
            price: item.GetMoney(PriceAttr),
            format: item.GetEnum<ListingFormat>(FormatAttr),
            status: item.GetEnum<ListingStatus>(StatusAttr),
            condition: item.GetString(ConditionAttr),
            city: item.GetString(CityAttr),
            photos: photos,
            attributes: item.GetStringMap(AttributesAttr),
            viewCount: item.GetLongOrDefault(ViewCountAttr),
            watcherCount: item.GetLongOrDefault(WatcherCountAttr),
            autoDeclineBelowPercent: item.GetDecimalOrNull(AutoDeclineAttr),
            createdAt: item.GetInstant(CreatedAtAttr),
            updatedAt: item.GetInstant(UpdatedAtAttr),
            publishedAt: item.GetInstantOrNull(PublishedAtAttr),
            version: item.GetLongOrDefault(VersionAttr),

            // Absent on rows written before auction terms were stored; read as "not set".
            auctionDurationDays: item.TryGetValue(AuctionDurationAttr, out var days) && days.N is { } n
                ? int.Parse(n, System.Globalization.CultureInfo.InvariantCulture)
                : null,
            auctionReservePrice: item.GetMoneyOrNull(AuctionReserveAttr),
            auctionBuyoutPrice: item.GetMoneyOrNull(AuctionBuyoutAttr),
            stock: item.TryGetValue(StockAttr, out var stockValue) && stockValue.N is { } stockText
                ? int.Parse(stockText, System.Globalization.CultureInfo.InvariantCulture)
                : 1);
    }
}
