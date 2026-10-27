using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>
/// What a draft may leave unfinished, what publishing demands, and what a seller may still
/// change once a listing has been live.
/// </summary>
public sealed class ListingEditTests
{
    private const string Seller = "seller-sub";
    private const string LongTitle = "PSA 10 Charizard Base Set Holo 1999";
    private static readonly ListingPhoto Photo = ListingPhoto.Create("listings/seller/one", 1200, 1200);

    private static Listing Draft(
        string title = LongTitle,
        long price = 2_500_000,
        string condition = "PSA 10",
        string city = "Bandung",
        ListingFormat format = ListingFormat.FixedPrice,
        TestClock? clock = null) => Listing.CreateDraft(
        Seller, "cards", title, Money.FromRupiah(price), format, condition, city, clock ?? TestClock.AtEpoch());

    private static Listing Live(
        ListingFormat format = ListingFormat.FixedPrice, TestClock? clock = null)
    {
        clock ??= TestClock.AtEpoch();
        var listing = Draft(format: format, clock: clock);
        listing.SetPhotos([Photo], clock);
        listing.Publish(clock);
        return listing;
    }

    private static readonly ListingPhoto[] OnePhoto = [ListingPhoto.Create("listings/seller/two", 800, 800)];

    // ─── Drafts: only a title and a category are required ────────────────────────

    [Fact]
    public void A_draft_can_be_saved_with_a_short_title_no_price_no_condition_no_city_and_no_photos()
    {
        var draft = Draft(title: "Kartu", price: 0, condition: string.Empty, city: "   ");

        draft.Status.ShouldBe(ListingStatus.Draft);
        draft.Title.ShouldBe("Kartu");
        draft.Price.ShouldBe(Money.Zero);
        draft.Condition.ShouldBeEmpty();
        draft.City.ShouldBeEmpty();
        draft.Photos.ShouldBeEmpty();
    }

    [Fact]
    public void A_draft_still_needs_a_non_blank_title() =>
        Should.Throw<ArgumentException>(() => Draft(title: "   "));

    [Fact]
    public void A_draft_title_still_has_a_ceiling() =>
        Should.Throw<DomainException>(() => Draft(title: new string('x', Listing.MaxTitleLength + 1)))
            .Code.ShouldBe("listing.title_too_long");

    [Fact]
    public void Editing_a_draft_keeps_the_draft_rules()
    {
        var draft = Draft();

        draft.EditDraft("cards", "Kartu", Money.Zero, ListingFormat.AcceptOffers, string.Empty, string.Empty, TestClock.AtEpoch());

        draft.Title.ShouldBe("Kartu");
        draft.Format.ShouldBe(ListingFormat.AcceptOffers);
        draft.City.ShouldBeEmpty();
    }

    // ─── Publishing keeps every rule the draft relaxed ───────────────────────────

    [Fact]
    public void Publishing_a_complete_draft_works()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(clock: clock);
        draft.SetPhotos([Photo], clock);

        draft.Publish(clock);

        draft.Status.ShouldBe(ListingStatus.Active);
    }

    [Fact]
    public void Publishing_refuses_a_title_under_the_public_minimum()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(title: "Kartu", clock: clock);
        draft.SetPhotos([Photo], clock);

        Should.Throw<DomainException>(() => draft.Publish(clock)).Code.ShouldBe("listing.title_too_short");
        draft.Status.ShouldBe(ListingStatus.Draft);
    }

    [Fact]
    public void Publishing_refuses_a_price_under_the_floor()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(price: 9_999, clock: clock);
        draft.SetPhotos([Photo], clock);

        Should.Throw<DomainException>(() => draft.Publish(clock)).Code.ShouldBe("listing.price_too_low");
    }

    [Fact]
    public void Publishing_refuses_a_missing_condition()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(condition: string.Empty, clock: clock);
        draft.SetPhotos([Photo], clock);

        Should.Throw<DomainException>(() => draft.Publish(clock)).Code.ShouldBe("listing.condition_required");
    }

    [Fact]
    public void Publishing_refuses_a_missing_city()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(city: string.Empty, clock: clock);
        draft.SetPhotos([Photo], clock);

        Should.Throw<DomainException>(() => draft.Publish(clock)).Code.ShouldBe("listing.city_required");
    }

    [Fact]
    public void Publishing_refuses_a_listing_with_no_photos() =>
        Should.Throw<DomainException>(() => Draft().Publish(TestClock.AtEpoch()))
            .Code.ShouldBe("listing.photo_required");

    // ─── A draft delisted before it was ever live is still a draft ───────────────

    [Fact]
    public void A_draft_that_was_archived_before_ever_being_published_can_still_be_edited_as_a_draft()
    {
        var clock = TestClock.AtEpoch();
        var draft = Draft(title: "Kartu", clock: clock);
        draft.Archive(clock);

        draft.IsUnpublished.ShouldBeTrue();
        draft.EditDraft("cards", LongTitle, Money.FromRupiah(500_000), ListingFormat.AcceptOffers, "Mint", "Jakarta", clock);

        draft.Title.ShouldBe(LongTitle);
    }

    [Fact]
    public void A_listing_that_has_been_live_can_no_longer_be_edited_as_a_draft()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);
        listing.Archive(clock);

        listing.IsUnpublished.ShouldBeFalse();
        Should.Throw<DomainException>(() =>
                listing.EditDraft("cards", "Another title entirely", Money.FromRupiah(500_000), ListingFormat.Auction, "Mint", "Jakarta", clock))
            .Code.ShouldBe("listing.not_editable");
    }

    // ─── Editing a listing that is or was live ───────────────────────────────────

    [Fact]
    public void A_live_listing_takes_a_new_description_photos_condition_city_and_price_in_one_version_step()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);
        var before = listing.Version;

        listing.EditLive(Money.FromRupiah(2_000_000), "  Sudut kartu sedikit tergores.  ", "PSA 9", "Surabaya", OnePhoto, auctionHasBids: false, clock);

        listing.Price.Rupiah.ShouldBe(2_000_000);
        listing.Description.ShouldBe("Sudut kartu sedikit tergores.");
        listing.Condition.ShouldBe("PSA 9");
        listing.City.ShouldBe("Surabaya");
        listing.Photos.Single().StorageKey.ShouldBe("listings/seller/two");
        listing.Version.ShouldBe(before + 1);
        listing.Status.ShouldBe(ListingStatus.Active);
    }

    [Fact]
    public void An_archived_listing_that_has_been_live_can_be_edited_and_stays_archived()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);
        listing.Archive(clock);

        listing.EditLive(listing.Price, "Deskripsi baru", listing.Condition, listing.City, OnePhoto, auctionHasBids: false, clock);

        listing.Description.ShouldBe("Deskripsi baru");
        listing.Status.ShouldBe(ListingStatus.Archived);
    }

    [Fact]
    public void Editing_never_touches_the_title_category_or_format()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);

        listing.EditLive(Money.FromRupiah(2_000_000), "x", "PSA 9", "Surabaya", OnePhoto, auctionHasBids: false, clock);

        listing.Title.ShouldBe(LongTitle);
        listing.CategoryId.ShouldBe("cards");
        listing.Format.ShouldBe(ListingFormat.FixedPrice);
    }

    [Fact]
    public void A_draft_is_not_edited_with_the_live_rules() =>
        Should.Throw<DomainException>(() =>
                Draft().EditLive(Money.FromRupiah(2_500_000), "x", "PSA 10", "Bandung", OnePhoto, false, TestClock.AtEpoch()))
            .Code.ShouldBe("listing.not_editable");

    [Fact]
    public void A_sold_listing_cannot_be_edited()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);
        listing.MarkSold(clock);

        Should.Throw<DomainException>(() =>
                listing.EditLive(listing.Price, "x", listing.Condition, listing.City, OnePhoto, false, clock))
            .Code.ShouldBe("listing.not_editable");
    }

    [Fact]
    public void A_suspended_listing_cannot_be_edited()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);
        listing.Suspend(clock);

        Should.Throw<DomainException>(() =>
                listing.EditLive(listing.Price, "x", listing.Condition, listing.City, OnePhoto, false, clock))
            .Code.ShouldBe("listing.not_editable");
    }

    [Fact]
    public void A_live_listing_keeps_at_least_one_photo() =>
        Should.Throw<DomainException>(() =>
                Live().EditLive(Money.FromRupiah(2_500_000), "x", "PSA 10", "Bandung", [], false, TestClock.AtEpoch()))
            .Code.ShouldBe("listing.photo_required");

    [Fact]
    public void A_live_listing_cannot_exceed_the_photo_limit()
    {
        var tooMany = Enumerable.Range(0, Listing.MaxPhotos + 1)
            .Select(i => ListingPhoto.Create($"listings/seller/p{i}", 100, 100))
            .ToList();

        Should.Throw<DomainException>(() =>
                Live().EditLive(Money.FromRupiah(2_500_000), "x", "PSA 10", "Bandung", tooMany, false, TestClock.AtEpoch()))
            .Code.ShouldBe("listing.too_many_photos");
    }

    [Theory]
    [InlineData("", "Bandung", "listing.condition_required")]
    [InlineData("PSA 10", "  ", "listing.city_required")]
    public void A_live_listing_keeps_a_condition_and_a_city(string condition, string city, string code) =>
        Should.Throw<DomainException>(() =>
                Live().EditLive(Money.FromRupiah(2_500_000), "x", condition, city, OnePhoto, false, TestClock.AtEpoch()))
            .Code.ShouldBe(code);

    [Fact]
    public void A_new_price_is_held_to_the_floor()
    {
        var listing = Live();

        Should.Throw<DomainException>(() =>
                listing.EditLive(Money.FromRupiah(9_999), "x", "PSA 10", "Bandung", OnePhoto, false, TestClock.AtEpoch()))
            .Code.ShouldBe("listing.price_too_low");
    }

    [Fact]
    public void A_refused_edit_leaves_the_listing_exactly_as_it_was()
    {
        var listing = Live();
        var before = (listing.Version, listing.Price, listing.Description, listing.Condition, listing.Photos.Count);

        Should.Throw<DomainException>(() =>
            listing.EditLive(Money.FromRupiah(3_000_000), "changed", "PSA 9", "   ", OnePhoto, false, TestClock.AtEpoch()));

        (listing.Version, listing.Price, listing.Description, listing.Condition, listing.Photos.Count).ShouldBe(before);
    }

    [Fact]
    public void An_auctions_starting_price_cannot_change()
    {
        var listing = Live(ListingFormat.Auction);

        Should.Throw<DomainException>(() =>
                listing.EditLive(Money.FromRupiah(3_000_000), "x", "PSA 10", "Bandung", OnePhoto, auctionHasBids: false, TestClock.AtEpoch()))
            .Code.ShouldBe("listing.auction_price_immutable");
    }

    [Fact]
    public void An_auction_without_bids_can_still_have_its_description_and_photos_corrected()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(ListingFormat.Auction, clock);

        listing.EditLive(listing.Price, "Deskripsi lebih jelas", "PSA 10", "Bandung", OnePhoto, auctionHasBids: false, clock);

        listing.Description.ShouldBe("Deskripsi lebih jelas");
    }

    [Fact]
    public void An_auction_with_bids_cannot_be_edited_at_all()
    {
        var listing = Live(ListingFormat.Auction);

        Should.Throw<DomainException>(() =>
                listing.EditLive(listing.Price, "x", "PSA 10", "Bandung", OnePhoto, auctionHasBids: true, TestClock.AtEpoch()))
            .Code.ShouldBe("auction.has_bids");
    }

    [Fact]
    public void Bids_on_a_fixed_price_flag_are_ignored_because_only_auctions_have_bids()
    {
        var clock = TestClock.AtEpoch();
        var listing = Live(clock: clock);

        listing.EditLive(listing.Price, "x", "PSA 10", "Bandung", OnePhoto, auctionHasBids: true, clock);

        listing.Description.ShouldBe("x");
    }
}
