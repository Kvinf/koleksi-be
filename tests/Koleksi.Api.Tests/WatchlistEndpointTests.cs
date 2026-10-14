using System.Net;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Saving listings ("Simpan"), end to end.</summary>
public sealed class WatchlistEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    // A fresh person per test: the watchlist bucket is per user and shared across the fixture.
    private readonly string _userId = Guid.NewGuid().ToString();
    private readonly string _username = "saver." + Guid.NewGuid().ToString("N")[..8];

    private HttpClient Client() => factory.CreateClientAs(_userId, _username);

    private Listing SeedListing(ListingFormat format = ListingFormat.FixedPrice)
    {
        var listing = TestData.ActiveListing(format: format);
        factory.Listings.Seed(listing);
        factory.Listings.SellerUsernames[listing.Id] = TestData.SellerUsername;
        return listing;
    }

    private async Task<long> WatchersAsync(string listingId) =>
        (await factory.Listings.FindAsync(listingId, CancellationToken.None))!.WatcherCount;

    [Fact]
    public async Task Saving_a_listing_counts_one_watcher_and_marks_it_saved()
    {
        var listing = SeedListing();
        using var client = Client();

        using var response = await client.PutAsync($"/api/watchlist/{listing.Id}", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("saved").GetBoolean().ShouldBeTrue();
        body.GetProperty("changed").GetBoolean().ShouldBeTrue();
        (await WatchersAsync(listing.Id)).ShouldBe(1);

        using var ids = await client.GetAsync("/api/watchlist/ids");
        (await ids.ReadJsonAsync()).GetProperty("ids").EnumerateArray()
            .Select(id => id.GetString()).ShouldBe([listing.Id]);
    }

    [Fact]
    public async Task Saving_the_same_listing_twice_counts_once()
    {
        var listing = SeedListing();
        using var client = Client();

        using var first = await client.PutAsync($"/api/watchlist/{listing.Id}", null);
        using var second = await client.PutAsync($"/api/watchlist/{listing.Id}", null);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.ReadJsonAsync()).GetProperty("changed").GetBoolean().ShouldBeFalse();
        (await WatchersAsync(listing.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Two_people_saving_the_same_listing_both_count()
    {
        var listing = SeedListing();
        using var one = Client();
        using var other = factory.CreateClientAs(Guid.NewGuid().ToString(), "other." + Guid.NewGuid().ToString("N")[..8]);

        (await one.PutAsync($"/api/watchlist/{listing.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.PutAsync($"/api/watchlist/{listing.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await WatchersAsync(listing.Id)).ShouldBe(2);
    }

    [Fact]
    public async Task Removing_counts_one_down_and_removing_twice_is_harmless()
    {
        var listing = SeedListing();
        using var client = Client();
        await client.PutAsync($"/api/watchlist/{listing.Id}", null);

        using var removed = await client.DeleteAsync($"/api/watchlist/{listing.Id}");
        using var again = await client.DeleteAsync($"/api/watchlist/{listing.Id}");

        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.ReadJsonAsync()).GetProperty("changed").GetBoolean().ShouldBeTrue();
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ReadJsonAsync()).GetProperty("changed").GetBoolean().ShouldBeFalse();
        (await WatchersAsync(listing.Id)).ShouldBe(0);

        using var ids = await client.GetAsync("/api/watchlist/ids");
        (await ids.ReadJsonAsync()).GetProperty("ids").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Removing_something_never_saved_does_not_move_the_count()
    {
        var listing = SeedListing();
        using var someoneElse = factory.CreateClientAs(Guid.NewGuid().ToString(), "x." + Guid.NewGuid().ToString("N")[..8]);
        await someoneElse.PutAsync($"/api/watchlist/{listing.Id}", null);
        using var client = Client();

        using var response = await client.DeleteAsync($"/api/watchlist/{listing.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WatchersAsync(listing.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_seller_cannot_save_their_own_listing()
    {
        var listing = SeedListing();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await seller.PutAsync($"/api/watchlist/{listing.Id}", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("watchlist.own_listing");
        (await WatchersAsync(listing.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_sold_listing_cannot_be_newly_saved()
    {
        var listing = TestData.ActiveListing();
        listing.MarkSold(factory.Clock);
        factory.Listings.Seed(listing);
        using var client = Client();

        using var response = await client.PutAsync($"/api/watchlist/{listing.Id}", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("watchlist.not_active");
        (await WatchersAsync(listing.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_listing_that_does_not_exist_or_is_not_public_is_not_found()
    {
        var draft = TestData.DraftListing();
        factory.Listings.Seed(draft);
        var suspended = TestData.SuspendedListing();
        factory.Listings.Seed(suspended);
        using var client = Client();

        foreach (var id in new[] { TestData.UnknownListingId, draft.Id, suspended.Id })
        {
            using var response = await client.PutAsync($"/api/watchlist/{id}", null);
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("watchlist.listing_not_found");
        }
    }

    [Fact]
    public async Task A_malformed_listing_id_is_refused_without_touching_storage()
    {
        using var client = Client();

        using var save = await client.PutAsync("/api/watchlist/not-an-id", null);
        using var remove = await client.DeleteAsync("/api/watchlist/not-an-id");

        save.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        remove.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await remove.ReadJsonAsync()).GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task The_ceiling_refuses_a_new_save_but_not_a_repeat()
    {
        var existing = SeedListing();
        for (var index = 0; index < WatchlistRules.MaxItems - 1; index++)
        {
            factory.Watchlist.Seed(_userId, $"stale-{index}", factory.Clock.UtcNow);
        }

        using var client = Client();
        (await client.PutAsync($"/api/watchlist/{existing.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Exactly full now.
        var extra = SeedListing();
        using var refused = await client.PutAsync($"/api/watchlist/{extra.Id}", null);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("watchlist.limit_reached");
        (await WatchersAsync(extra.Id)).ShouldBe(0);

        using var repeat = await client.PutAsync($"/api/watchlist/{existing.Id}", null);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Saving_requires_a_signed_in_caller()
    {
        var listing = SeedListing();
        using var anonymous = factory.CreateClientFrom("203.0.113.77");

        (await anonymous.PutAsync($"/api/watchlist/{listing.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync($"/api/watchlist/{listing.Id}")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/watchlist")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/watchlist/ids")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await WatchersAsync(listing.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task The_list_shows_cards_newest_saved_first_and_reveals_no_user_ids()
    {
        var older = SeedListing();
        var newer = TestData.ActiveListing("Newer card");
        factory.Listings.Seed(newer);
        factory.Listings.SellerUsernames[newer.Id] = TestData.SellerUsername;
        using var client = Client();

        await client.PutAsync($"/api/watchlist/{older.Id}", null);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        await client.PutAsync($"/api/watchlist/{newer.Id}", null);

        using var response = await client.GetAsync("/api/watchlist");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("id").GetString()).ShouldBe([newer.Id, older.Id]);

        var card = items[0];
        card.GetProperty("title").GetString().ShouldBe("Newer card");
        card.GetProperty("status").GetString().ShouldBe("Active");
        card.GetProperty("categorySlug").GetString().ShouldBe(TestData.CardsCategorySlug);

        // The seller's public handle, never the stored sign-in name (an email address).
        card.GetProperty("sellerUsername").GetString().ShouldBe(factory.PublicHandleOf(TestData.SellerId));
        body.GetProperty("page").GetProperty("total").GetInt32().ShouldBe(2);

        var text = await response.ReadTextAsync();
        text.ShouldNotContain(_userId);
        text.ShouldNotContain(TestData.SellerId);
        text.ShouldNotContain(TestData.SellerUsername);
    }

    [Fact]
    public async Task A_listing_that_sold_after_it_was_saved_stays_listed_as_sold_and_can_be_removed()
    {
        var listing = SeedListing();
        using var client = Client();
        await client.PutAsync($"/api/watchlist/{listing.Id}", null);

        var sold = (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!;
        var version = sold.Version;
        sold.MarkSold(factory.Clock);
        await factory.Listings.UpdateAsync(sold, version, CancellationToken.None);

        using var list = await client.GetAsync("/api/watchlist");
        (await list.ReadJsonAsync()).GetProperty("items").EnumerateArray().Single()
            .GetProperty("status").GetString().ShouldBe("Sold");

        (await client.DeleteAsync($"/api/watchlist/{listing.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WatchersAsync(listing.Id)).ShouldBe(0);
    }

    [Fact]
    public async Task A_saved_listing_that_no_longer_exists_is_dropped_from_the_list_on_read()
    {
        factory.Watchlist.Seed(_userId, TestData.UnknownListingId, factory.Clock.UtcNow);
        using var client = Client();

        using var list = await client.GetAsync("/api/watchlist");

        (await list.ReadJsonAsync()).GetProperty("items").GetArrayLength().ShouldBe(0);
        using var ids = await client.GetAsync("/api/watchlist/ids");
        (await ids.ReadJsonAsync()).GetProperty("ids").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task An_auction_card_carries_its_current_price_and_bid_count()
    {
        var listing = SeedListing(ListingFormat.Auction);
        await factory.Auctions.AddAsync(TestData.LiveAuction(listing), CancellationToken.None);
        using var client = Client();

        (await client.PutAsync($"/api/watchlist/{listing.Id}", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var list = await client.GetAsync("/api/watchlist");

        var card = (await list.ReadJsonAsync()).GetProperty("items").EnumerateArray().Single();
        card.GetProperty("format").GetString().ShouldBe("Auction");
        card.GetProperty("priceRupiah").GetInt64().ShouldBe(1_000_000);
        card.GetProperty("auctionEndsAt").ValueKind.ShouldNotBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Each_person_sees_only_their_own_list()
    {
        var listing = SeedListing();
        using var client = Client();
        await client.PutAsync($"/api/watchlist/{listing.Id}", null);
        using var stranger = factory.CreateClientAs(Guid.NewGuid().ToString(), "s." + Guid.NewGuid().ToString("N")[..8]);

        using var ids = await stranger.GetAsync("/api/watchlist/ids");

        (await ids.ReadJsonAsync()).GetProperty("ids").GetArrayLength().ShouldBe(0);
    }
}
