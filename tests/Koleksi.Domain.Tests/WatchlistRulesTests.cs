using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class WatchlistRulesTests
{
    private const string Seller = "seller-sub";
    private const string Buyer = "buyer-sub";

    private static Listing ListingInStatus(ListingStatus status)
    {
        var clock = TestClock.AtEpoch();
        var listing = Listing.CreateDraft(
            Seller, "cards", "PSA 10 Charizard", Money.FromRupiah(2_500_000), ListingFormat.FixedPrice,
            "PSA 10", "Bandung", clock);

        if (status == ListingStatus.Draft)
        {
            return listing;
        }

        listing.SetPhotos([ListingPhoto.Create("listing/photo.jpg", 1200, 1200)], clock);
        listing.Publish(clock);

        switch (status)
        {
            case ListingStatus.Sold:
                listing.MarkSold(clock);
                break;
            case ListingStatus.Archived:
                listing.Archive(clock);
                break;
            case ListingStatus.Suspended:
                listing.Suspend(clock);
                break;
        }

        return listing;
    }

    [Fact]
    public void An_active_listing_of_someone_else_can_be_saved() =>
        Should.NotThrow(() => WatchlistRules.EnsureCanSave(ListingInStatus(ListingStatus.Active), Buyer, false, 0));

    [Fact]
    public void A_seller_cannot_save_their_own_listing() =>
        Should.Throw<DomainException>(() =>
                WatchlistRules.EnsureCanSave(ListingInStatus(ListingStatus.Active), Seller, false, 0))
            .Code.ShouldBe("watchlist.own_listing");

    [Theory]
    [InlineData(ListingStatus.Sold)]
    [InlineData(ListingStatus.Archived)]
    [InlineData(ListingStatus.Suspended)]
    [InlineData(ListingStatus.Draft)]
    public void Only_an_active_listing_can_be_newly_saved(ListingStatus status) =>
        Should.Throw<DomainException>(() =>
                WatchlistRules.EnsureCanSave(ListingInStatus(status), Buyer, false, 0))
            .Code.ShouldBe("watchlist.not_active");

    [Fact]
    public void The_ceiling_stops_a_new_save_but_the_last_slot_is_usable()
    {
        var listing = ListingInStatus(ListingStatus.Active);

        Should.NotThrow(() => WatchlistRules.EnsureCanSave(listing, Buyer, false, WatchlistRules.MaxItems - 1));
        Should.Throw<DomainException>(() =>
                WatchlistRules.EnsureCanSave(listing, Buyer, false, WatchlistRules.MaxItems))
            .Code.ShouldBe("watchlist.limit_reached");
    }

    [Fact]
    public void Saving_again_what_is_saved_is_allowed_at_the_ceiling_and_after_it_sold() =>
        Should.NotThrow(() =>
            WatchlistRules.EnsureCanSave(ListingInStatus(ListingStatus.Sold), Buyer, true, WatchlistRules.MaxItems));
}
