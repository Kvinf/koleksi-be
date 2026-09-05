using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// Aggregates for the storage tests, built through the domain's own factories.
/// </summary>
/// <remarks>
/// Assembled with the real constructors and mutators rather than field by field. A fixture
/// that bypasses the invariants can express a state the product cannot reach, and a
/// storage test built on one proves the row round-trips but not that anything ever writes
/// such a row.
/// </remarks>
internal static class Fixtures
{
    public const string SellerId = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    public const string SellerUsername = "budi.collectibles";
    public const string BidderId = "9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";
    public const string BidderUsername = "rangga.wh";
    public const string RivalId = "1d7b6e30-5c48-4a2f-9e83-70b1c4f5a266";
    public const string RivalUsername = "sinta.kolektor";

    public const string CardsCategory = "cards";

    public static readonly IClock Clock = new FixedClock();

    /// <summary>A published fixed-price card listing.</summary>
    public static Listing ActiveListing(
        string title = "PSA 10 Charizard Base Set Holo 1999",
        long priceRupiah = 2_500_000,
        string city = "Bandung",
        string grade = "PSA 10",
        string set = "Base Set",
        ListingFormat format = ListingFormat.FixedPrice)
    {
        var listing = Draft(title, priceRupiah, city, grade, set, format);
        listing.Publish(Clock);
        return listing;
    }

    public static Listing DraftListing(string title = "Sealed Gunpla RG Nu Gundam Ver Ka") =>
        Draft(title, 1_800_000, "Jakarta", "Raw", "Neo Genesis", ListingFormat.FixedPrice);

    public static Auction LiveAuction(
        Listing listing, long startingBidRupiah = 1_000_000, TimeSpan? duration = null)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var auction = Auction.Create(
            listing.Id,
            listing.SellerId,
            Money.FromRupiah(startingBidRupiah),
            duration ?? TimeSpan.FromDays(3),
            Clock);

        auction.Open(Clock);
        return auction;
    }

    public static Offer PendingOffer(Listing listing, long amountRupiah = 2_000_000) => Offer.Create(
        listing.Id,
        BidderId,
        listing.SellerId,
        Money.FromRupiah(amountRupiah),
        listing.Price,
        message: "Bisa nego dikit?",
        autoDeclineBelowPercent: null,
        Clock);

    public static Order SampleOrder(Listing listing, Money? unitPrice = null) => Order.Create(
        BidderId,
        listing.SellerId,
        [new OrderLine(listing.Id, listing.Title, "listing/photo-one.jpg", unitPrice ?? listing.Price, 1)],
        new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
        new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
        Clock);

    private static Listing Draft(
        string title, long priceRupiah, string city, string grade, string set, ListingFormat format)
    {
        var listing = Listing.CreateDraft(
            SellerId,
            CardsCategory,
            title,
            Money.FromRupiah(priceRupiah),
            format,
            grade,
            city,
            Clock);

        listing.SetDescription("Kondisi mulus, kartu asli, sudah di-grading.", Clock);
        listing.SetPhotos([ListingPhoto.Create("listing/photo-one.jpg", 1200, 1200)], Clock);
        listing.SetAttributes(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grade"] = grade,
                ["set"] = set,
            },
            Clock);

        return listing;
    }
}
