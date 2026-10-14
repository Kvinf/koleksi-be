using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Three seller/buyer safety rules that were missing: an auction draft remembers its terms, a
/// delisted auction stops taking bids (or is refused when it has them), and an item cannot be
/// checked out by two buyers at once.
/// </summary>
public sealed class ListingLifecycleTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static object AuctionRequest(
        string ownerId, bool publish, int days = 7, long? reserve = 1_500_000, long? buyout = 3_000_000) => new
    {
        categoryId = TestData.CardsCategoryId,
        title = "PSA 10 Charizard Base Set Holo 1999",
        priceRupiah = 1_000_000,
        format = "Auction",
        condition = "PSA 10",
        city = "Bandung",
        description = "Lelang kartu langka.",
        photos = new[] { new { storageKey = TestData.PhotoKey(ownerId, "test-photo"), width = 1200, height = 1200 } },
        auctionDurationDays = days,
        reservePriceRupiah = reserve,
        buyoutPriceRupiah = buyout,
        publish,
    };

    // A seller per test: the idempotency store and listing store are shared by the fixture.
    private static string NewSellerId() => "01JSELLER" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();

    // ─── Auction drafts keep their terms ─────────────────────────────────────────

    [Fact]
    public async Task An_auction_draft_remembers_duration_reserve_and_buyout_when_it_is_opened_for_editing()
    {
        var sellerId = NewSellerId();
        using var seller = factory.CreateClientAs(sellerId, "seller.draftterms");

        using var created = await seller.PostAsJsonAsync("/api/listings", AuctionRequest(sellerId, publish: false));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;

        using var edit = await seller.GetAsync($"/api/listings/{id}");

        edit.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await edit.ReadJsonAsync();
        body.GetProperty("auctionDurationDays").GetInt32().ShouldBe(7);
        body.GetProperty("reservePriceRupiah").GetInt64().ShouldBe(1_500_000);
        body.GetProperty("buyoutPriceRupiah").GetInt64().ShouldBe(3_000_000);
    }

    [Fact]
    public async Task Editing_a_draft_replaces_the_stored_terms_and_a_non_auction_format_clears_them()
    {
        var sellerId = NewSellerId();
        using var seller = factory.CreateClientAs(sellerId, "seller.editterms");
        using var created = await seller.PostAsJsonAsync("/api/listings", AuctionRequest(sellerId, publish: false));
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;

        using var changed = await seller.PutAsJsonAsync($"/api/listings/{id}", AuctionRequest(sellerId, publish: false, days: 5, reserve: null, buyout: null));
        changed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var afterEdit = await (await seller.GetAsync($"/api/listings/{id}")).ReadJsonAsync();
        afterEdit.GetProperty("auctionDurationDays").GetInt32().ShouldBe(5);
        afterEdit.TryGetProperty("reservePriceRupiah", out _).ShouldBeFalse();

        var fixedPrice = new
        {
            categoryId = TestData.CardsCategoryId, title = "PSA 10 Charizard Base Set Holo 1999", priceRupiah = 1_000_000,
            format = "FixedPrice", condition = "PSA 10", city = "Bandung", description = "x",
            photos = new[] { new { storageKey = TestData.PhotoKey(sellerId, "test-photo"), width = 1200, height = 1200 } },
            publish = false,
        };
        using var toFixed = await seller.PutAsJsonAsync($"/api/listings/{id}", fixedPrice);
        toFixed.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cleared = await (await seller.GetAsync($"/api/listings/{id}")).ReadJsonAsync();
        cleared.TryGetProperty("auctionDurationDays", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Publishing_a_saved_auction_draft_opens_an_auction_with_the_saved_terms()
    {
        var sellerId = NewSellerId();
        using var seller = factory.CreateClientAs(sellerId, "seller.publishdraft");
        using var created = await seller.PostAsJsonAsync("/api/listings", AuctionRequest(sellerId, publish: false, days: 7));
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;

        using var published = await seller.PostAsync($"/api/listings/{id}/publish", null);

        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auction = await factory.Auctions.FindAsync(id, CancellationToken.None);
        auction.ShouldNotBeNull();
        auction!.Status.ShouldBe(AuctionStatus.Live);
        auction.ReservePrice!.Value.Rupiah.ShouldBe(1_500_000);
        auction.BuyoutPrice!.Value.Rupiah.ShouldBe(3_000_000);
        (auction.EndsAt - auction.StartsAt).ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public async Task Auction_terms_that_break_an_auction_rule_refuse_the_publish_and_leave_no_live_listing()
    {
        var sellerId = NewSellerId();
        using var seller = factory.CreateClientAs(sellerId, "seller.badterms");

        // Reserve above the buyout is not a valid auction.
        using var response = await seller.PostAsJsonAsync("/api/listings", AuctionRequest(sellerId, publish: true, reserve: 4_000_000, buyout: 3_000_000));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.invalid_reserve");

        var mine = await (await seller.GetAsync("/api/listings/mine")).ReadJsonAsync();
        mine.GetProperty("items").EnumerateArray()
            .ShouldNotContain(item => item.GetProperty("status").GetString() == "Active");
    }

    // ─── Delisting an auction ────────────────────────────────────────────────────

    [Fact]
    public async Task Delisting_an_auction_without_bids_cancels_it_so_nobody_can_bid_on_a_listing_that_is_gone()
    {
        var (listing, sellerId) = SeedLiveAuction();
        using var seller = factory.CreateClientAs(sellerId, "seller.delist");

        using var archived = await seller.PostAsync($"/api/listings/{listing.Id}/archive", null);

        archived.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.Auctions.FindAsync(listing.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Cancelled);

        using var bidder = factory.CreateClientAs(Guid.NewGuid().ToString(), "late.bidder");
        using var bid = await bidder.PostBidAsync(listing.Id, 2_000_000);
        bid.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        // D-09: the bid path now checks the listing first, so the refusal names the listing.
        (await bid.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.listing_unavailable");
    }

    // ─── Delisting while a buyer is paying (QA D-01) ─────────────────────────────

    [Fact]
    public async Task Delisting_an_item_a_buyer_is_paying_for_is_refused_and_it_stays_on_sale()
    {
        var listing = SeedFixedPriceListing();
        var held = await factory.Holds.TryHoldAsync(listing.Id, "buyer-1", "order-1", CancellationToken.None);
        held.Acquired.ShouldBeTrue();
        using var seller = factory.CreateClientAs(listing.SellerId, "seller.hold");

        using var refused = await seller.PostAsync($"/api/listings/{listing.Id}/archive", null);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.has_pending_sale");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Status.ShouldBe(ListingStatus.Active);
    }

    [Fact]
    public async Task Delisting_works_again_once_the_buyers_hold_is_released()
    {
        var listing = SeedFixedPriceListing();
        await factory.Holds.TryHoldAsync(listing.Id, "buyer-2", "order-2", CancellationToken.None);
        await factory.Holds.ReleaseAsync(listing.Id, "buyer-2", "order-2", CancellationToken.None);
        using var seller = factory.CreateClientAs(listing.SellerId, "seller.released");

        using var archived = await seller.PostAsync($"/api/listings/{listing.Id}/archive", null);

        archived.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delisting_an_auction_that_has_bids_is_refused_and_the_listing_stays_live()
    {
        var (listing, sellerId) = SeedLiveAuction();
        using var bidder = factory.CreateClientAs(Guid.NewGuid().ToString(), "first.bidder");
        (await bidder.PostBidAsync(listing.Id, 1_200_000)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var seller = factory.CreateClientAs(sellerId, "seller.hasbids");
        using var archived = await seller.PostAsync($"/api/listings/{listing.Id}/archive", null);

        archived.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await archived.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.has_bids");
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Status.ShouldBe(ListingStatus.Active);
        (await factory.Auctions.FindAsync(listing.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Live);
    }

    [Fact]
    public async Task Relisting_a_delisted_auction_opens_a_fresh_one()
    {
        var (listing, sellerId) = SeedLiveAuction();
        using var seller = factory.CreateClientAs(sellerId, "seller.relist");
        (await seller.PostAsync($"/api/listings/{listing.Id}/archive", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var relisted = await seller.PostAsync($"/api/listings/{listing.Id}/publish", null);

        relisted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var auction = await factory.Auctions.FindAsync(listing.Id, CancellationToken.None);
        auction!.Status.ShouldBe(AuctionStatus.Live);
        auction.BidCount.ShouldBe(0);

        using var bidder = factory.CreateClientAs(Guid.NewGuid().ToString(), "relist.bidder");
        (await bidder.PostBidAsync(listing.Id, 1_100_000)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── One buyer at a time per item ────────────────────────────────────────────

    [Fact]
    public async Task A_second_buyer_cannot_check_out_an_item_the_first_is_paying_for()
    {
        var listing = SeedFixedPriceListing();
        using var first = factory.CreateClientAs(Guid.NewGuid().ToString(), "first.buyer");
        using var second = factory.CreateClientAs(Guid.NewGuid().ToString(), "second.buyer");

        using var firstResponse = await first.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
        using var secondResponse = await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await secondResponse.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.item_on_hold");
    }

    [Fact]
    public async Task The_item_is_free_again_once_the_first_buyers_payment_window_has_passed()
    {
        var listing = SeedFixedPriceListing();
        using var first = factory.CreateClientAs(Guid.NewGuid().ToString(), "lapse.first");
        using var second = factory.CreateClientAs(Guid.NewGuid().ToString(), "lapse.second");
        (await first.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Clock.UtcNow += IListingHoldStore.Duration + TimeSpan.FromSeconds(1);
        try
        {
            using var response = await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task Checking_out_the_same_item_again_replaces_your_earlier_unpaid_order_instead_of_duplicating_it()
    {
        var listing = SeedFixedPriceListing();
        var buyerId = Guid.NewGuid().ToString();
        using var buyer = factory.CreateClientAs(buyerId, "repeat.buyer");

        using var one = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
        var firstOrderId = (await one.ReadJsonAsync()).GetProperty("orderId").GetString()!;
        using var two = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
        var secondOrderId = (await two.ReadJsonAsync()).GetProperty("orderId").GetString()!;

        secondOrderId.ShouldNotBe(firstOrderId);
        (await factory.Orders.FindAsync(firstOrderId, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Cancelled);
        (await factory.Orders.FindAsync(secondOrderId, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public async Task A_cart_checkout_refuses_the_whole_basket_when_one_item_is_held_by_someone_else_and_keeps_nothing_reserved()
    {
        var contested = SeedFixedPriceListing();
        var other = SeedFixedPriceListing();
        using var rival = factory.CreateClientAs(Guid.NewGuid().ToString(), "cart.rival");
        (await rival.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", contested.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var buyer = factory.CreateClientAs(Guid.NewGuid().ToString(), "cart.buyer");
        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { other.Id, contested.Id },
            shipTo = new
            {
                recipientName = "Rangga Wijaya", phone = "081234567890", street = "Jl. Merdeka No. 1",
                city = "Bandung", province = "Jawa Barat", postalCode = "40111",
            },
            courierCodeByShop = new Dictionary<string, string> { [factory.PublicHandleOf(other.SellerId)] = "jne" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.item_on_hold");

        // The refused basket took nothing: the other item is still free for anyone.
        using var third = factory.CreateClientAs(Guid.NewGuid().ToString(), "cart.third");
        (await third.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", other.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_accepted_offer_on_an_item_that_is_no_longer_for_sale_cannot_be_checked_out()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        var buyerId = Guid.NewGuid().ToString();
        var offer = Koleksi.Domain.Offers.Offer.Create(
            listing.Id, buyerId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa?", autoDeclineBelowPercent: null, factory.Clock);
        offer.Accept(listing.SellerId, factory.Clock);
        factory.Offers.Seed(offer);

        listing.Archive(factory.Clock);
        factory.Listings.Seed(listing);

        using var buyer = factory.CreateClientAs(buyerId, "offer.late");
        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AcceptedOffer", offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static object CheckoutBody(string source, string referenceId) => new
    {
        source,
        referenceId,
        shipTo = new
        {
            recipientName = "Rangga Wijaya", phone = "081234567890", street = "Jl. Merdeka No. 1",
            city = "Bandung", province = "Jawa Barat", postalCode = "40111",
        },
        courierCode = "jne",
    };

    private Listing SeedFixedPriceListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        return listing;
    }

    private (Listing Listing, string SellerId) SeedLiveAuction()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);
        factory.Listings.Seed(listing);
        factory.Auctions.Seed(TestData.LiveAuction(listing, Money.FromRupiah(1_000_000)));
        return (listing, listing.SellerId);
    }
}
