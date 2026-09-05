using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Api.Tests.Fakes;

namespace Koleksi.Api.Tests;

/// <summary>
/// Builds the aggregates the endpoint tests read back.
/// </summary>
/// <remarks>
/// Everything is created through the domain's own factories and then moved into place
/// with its own methods, never assembled field by field. A fixture that bypasses the
/// invariants can express a state the product cannot reach, and a test built on one
/// proves nothing about the running system.
/// </remarks>
public static class TestData
{
    /// <summary>Cognito subjects. These are pool identifiers, not entity ids.</summary>
    public const string SellerId = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";

    public const string SellerUsername = "budi.collectibles";

    /// <summary>Matches <see cref="TokenOptions"/>'s default subject.</summary>
    public const string BidderId = "01JBIDDERAAAAAAAAAAAAAAAAA";

    public const string BidderUsername = "rangga.wh";

    public const string RivalId = "9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";

    public const string RivalUsername = "sinta.kolektor";

    public const string StaffId = "5f1c8d40-72ab-4e93-8c15-6b0d3a9e4711";

    /// <summary>The seeded trading-card category, which the item routes live under.</summary>
    public const string CardsCategoryId = "cards";

    public const string CardsCategorySlug = "kartu";

    /// <summary>An id that is well-formed but belongs to nothing.</summary>
    public static string UnknownListingId => EntityId.New(MutableClock.Origin);

    /// <summary>An active fixed-price listing owned by <see cref="SellerId"/>.</summary>
    public static Listing ActiveListing(
        string title = "PSA 10 Charizard Base Set Holo 1999",
        Money? price = null,
        ListingFormat format = ListingFormat.FixedPrice)
    {
        var listing = Draft(title, price, format);
        listing.SetPhotos([ListingPhoto.Create("listing/photo-one.jpg", 1200, 1200)], Clock);
        listing.Publish(Clock);
        return listing;
    }

    /// <summary>An unpublished listing. Visible to its seller and to staff, to nobody else.</summary>
    public static Listing DraftListing(string title = "Sealed Gunpla RG Nu Gundam Ver Ka") =>
        Draft(title, null, ListingFormat.FixedPrice);

    /// <summary>A listing removed by moderation. Invisible to its own seller by design.</summary>
    public static Listing SuspendedListing(string title = "Counterfeit Kaws Companion Figure")
    {
        var listing = ActiveListing(title);
        listing.Suspend(Clock);
        return listing;
    }

    /// <summary>A live auction on <paramref name="listing"/>, open and taking bids.</summary>
    public static Auction LiveAuction(
        Listing listing,
        Money? startingBid = null,
        TimeSpan? duration = null,
        Money? reservePrice = null,
        Money? buyoutPrice = null)
    {
        ArgumentNullException.ThrowIfNull(listing);

        var auction = Auction.Create(
            listing.Id,
            listing.SellerId,
            startingBid ?? Money.FromRupiah(1_000_000),
            duration ?? TimeSpan.FromDays(3),
            Clock,
            reservePrice,
            buyoutPrice);

        auction.Open(Clock);
        return auction;
    }

    private static Listing Draft(string title, Money? price, ListingFormat format)
    {
        var listing = Listing.CreateDraft(
            SellerId,
            CardsCategoryId,
            title,
            price ?? Money.FromRupiah(2_500_000),
            format,
            "PSA 10",
            "Bandung",
            Clock);

        listing.SetDescription("Kondisi mulus, kartu asli, sudah di-grading.", Clock);
        listing.SetAttributes(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grade"] = "PSA 10",
                ["set"] = "Base Set",
            },
            Clock);

        return listing;
    }

    /// <summary>
    /// A clock fixed at the same instant the test host starts from.
    /// </summary>
    /// <remarks>
    /// Separate from the host's <see cref="MutableClock"/> on purpose: fixtures are built
    /// before a test moves time forward, and a shared instance would make the age of a
    /// listing depend on the order assertions happen to run in.
    /// </remarks>
    private static readonly IClock Clock = new FixedClock(MutableClock.Origin);

    private sealed class FixedClock(DateTimeOffset instant) : IClock
    {
        public DateTimeOffset UtcNow { get; } = instant;
    }
}
