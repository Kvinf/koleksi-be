using System.Net;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Bidding, end to end.
/// </summary>
/// <remarks>
/// Two things are being checked at once, and the second is the one that would go unnoticed
/// without a test at this level: that the rules hold, and that the response says nothing
/// it should not — a rival's identity, in particular, has no business reaching another
/// bidder's client. Some assertions are made against the raw response text rather than a
/// typed model, which can only show the fields someone remembered to declare.
/// </remarks>
public sealed class AuctionEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task An_opening_bid_settles_at_exactly_the_amount_bid()
    {
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(listingId, 4_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();
        body.GetProperty("isLeading").GetBoolean().ShouldBeTrue();
        body.GetProperty("currentPriceRupiah").GetInt64().ShouldBe(4_000_000);
    }

    [Fact]
    public async Task A_bid_updates_the_price_a_browse_card_would_show()
    {
        var (listingId, listing) = Seed();

        // The browse projection is a separate copy of the price, kept for the home page
        // and dashboard cards — it does not follow the auction automatically, so a bid
        // has to explicitly catch it up.
        factory.SearchIndex.Seed(new ListingSummary(
            listingId, listing.Title, listing.CategoryId, TestData.CardsCategorySlug,
            PriceRupiah: 1_000_000, ListingFormat.Auction, ListingStatus.Active, listing.Condition,
            TestData.SellerUsername, listing.City, PrimaryPhotoKey: null,
            AuctionEndsAt: DateTimeOffset.UtcNow.AddDays(3), BidCount: 0));

        // A subject of its own, not TestData.BidderId — the bidding rate limiter is keyed
        // per subject and shared across every test in this class via IClassFixture, and a
        // constant already spent by a dozen other tests has no budget left to spend here.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "priceUpdateBidder");
        using var response = await client.PostBidAsync(listingId, 4_000_000);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var indexed = factory.SearchIndex.Items.ShouldHaveSingleItem();
        indexed.PriceRupiah.ShouldBe(4_000_000);
        indexed.BidCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_response_never_carries_the_previous_leaders_identity()
    {
        var (listingId, _) = Seed();

        using var leader = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var opening = await leader.PostBidAsync(listingId, 4_000_000);
        opening.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var challenger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);
        using var response = await challenger.PostBidAsync(listingId, 4_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var text = await response.ReadTextAsync();
        text.ShouldNotContain(TestData.BidderId);
        text.ShouldNotContain(TestData.BidderUsername);
        text.ToUpperInvariant().ShouldNotContain("OUTBID");
    }

    [Fact]
    public async Task A_bid_that_does_not_clear_the_next_minimum_is_refused_outright()
    {
        var (listingId, _) = Seed();

        using var leader = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var opening = await leader.PostBidAsync(listingId, 4_000_000);
        opening.StatusCode.ShouldBe(HttpStatusCode.OK);

        // No proxy defence to fall back on — a bid below the current price plus the
        // minimum increment is simply rejected, not settled somewhere in between.
        using var challenger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);
        using var response = await challenger.PostBidAsync(listingId, 4_010_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.bid_too_low");
    }

    [Fact]
    public async Task A_leading_bidder_cannot_bid_again_while_still_leading()
    {
        var (listingId, _) = Seed();

        // A subject of its own — see A_bid_updates_the_price_a_browse_card_would_show for
        // why TestData.BidderId's shared rate-limit budget cannot absorb another two calls.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "repeatBidder");

        using var opening = await client.PostBidAsync(listingId, 4_000_000);
        opening.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await client.PostBidAsync(listingId, 5_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.already_leading");
    }

    [Fact]
    public async Task A_seller_cannot_bid_on_their_own_lot()
    {
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostBidAsync(listingId, 4_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.seller_cannot_bid");
    }

    [Fact]
    public async Task A_bid_below_the_next_minimum_is_refused()
    {
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(listingId, 999_999);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.bid_too_low");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task A_negative_maximum_is_not_an_amount_at_all(long maximum)
    {
        // Refused by the money type before any auction rule is consulted. There is no
        // annotation on the request record doing this — minimal APIs would not run one —
        // so this is the check that actually holds.
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(listingId, maximum);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.invalid_amount");
    }

    [Fact]
    public async Task A_maximum_of_zero_is_a_bid_that_is_simply_too_low()
    {
        // Zero rupiah is a legitimate Money — it is the price of nothing, not a malformed
        // number — so the auction refuses it on its own terms rather than the type's.
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(listingId, 0);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.bid_too_low");
    }

    [Fact]
    public async Task A_bid_on_an_auction_that_does_not_exist_is_a_404()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(TestData.UnknownListingId, 4_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_bid_that_loses_a_race_is_retried_rather_than_failed()
    {
        // Two people bidding on the same lot in the same second is normal traffic, not an
        // error. The handler re-reads and re-runs the rules; it never merges, because
        // merging two bids means inventing a price neither bidder offered.
        var (listingId, _) = Seed();
        factory.Auctions.ConflictsToInject = 2;

        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(listingId, 4_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Auctions.ConflictsToInject.ShouldBe(0);
    }

    [Fact]
    public async Task Sustained_contention_is_reported_as_a_conflict_not_a_crash()
    {
        var (listingId, _) = Seed();
        factory.Auctions.ConflictsToInject = 99;

        try
        {
            using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

            using var response = await client.PostBidAsync(listingId, 4_000_000);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.too_busy");
        }
        finally
        {
            factory.Auctions.ConflictsToInject = 0;
        }
    }

    [Fact]
    public async Task The_item_page_masks_every_bidder_in_the_history()
    {
        var (listingId, listing) = Seed();

        using var leader = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var first = await leader.PostBidAsync(listingId, 4_000_000);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anonymous = factory.CreateClient();
        using var response = await anonymous.GetAsync(
            $"/api/catalog/listings/{listing.UrlPath(TestData.CardsCategorySlug).Split('/')[^1]}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var text = await response.ReadTextAsync();

        // Bid history is public on every auction page. Unmasked, it would publish who is
        // spending what, and make a shill ring trivial to coordinate.
        text.ShouldNotContain(TestData.BidderUsername);
        text.ShouldNotContain(TestData.BidderId);

        var auction = (await response.ReadJsonAsync()).GetProperty("auction");
        auction.GetProperty("viewerIsLeading").GetBoolean().ShouldBeFalse();
        auction.GetProperty("history").EnumerateArray()
            .ShouldAllBe(row => row.GetProperty("maskedBidder").GetString()!.Contains('•'));
    }

    [Fact]
    public async Task An_auction_page_is_never_stored_by_any_cache()
    {
        var (_, listing) = Seed();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/catalog/listings/{listing.UrlPath(TestData.CardsCategorySlug).Split('/')[^1]}");

        // The price changes with every bid, and a stale price is a bidder placing a bid
        // that is already too low.
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task A_reserve_not_yet_met_is_visible_on_the_item_page()
    {
        var (listingId, listing) = Seed(reservePrice: Money.FromRupiah(5_000_000));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync(
            $"/api/catalog/listings/{listing.UrlPath(TestData.CardsCategorySlug).Split('/')[^1]}");

        var auction = (await response.ReadJsonAsync()).GetProperty("auction");
        auction.GetProperty("reservePriceRupiah").GetInt64().ShouldBe(5_000_000);
        auction.GetProperty("reserveMet").GetBoolean().ShouldBeFalse();

        _ = listingId;
    }

    [Fact]
    public async Task Buyout_ends_the_auction_immediately_at_the_buyout_price()
    {
        var (listingId, _) = Seed(buyoutPrice: Money.FromRupiah(3_000_000));
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBuyoutAsync(listingId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("finalPriceRupiah").GetInt64().ShouldBe(3_000_000);

        var stored = await factory.Auctions.FindAsync(listingId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.Status.ShouldBe(AuctionStatus.Ended);
    }

    [Fact]
    public async Task Buyout_stays_available_after_bids_have_already_landed()
    {
        // Confirmed product decision: buyout is not withdrawn once bidding starts. A buyer
        // who wants certainty can always pay the seller's named price instead of continuing
        // to bid, and the previous leader simply loses — there is no notification system
        // yet to tell them, the same gap PlaceBid's own OutbidUserId is meant to close.
        var (listingId, _) = Seed(buyoutPrice: Money.FromRupiah(3_000_000));

        using var earlyBidder = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);
        using var bid = await earlyBidder.PostBidAsync(listingId, 1_500_000);
        bid.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var response = await buyer.PostBuyoutAsync(listingId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stored = await factory.Auctions.FindAsync(listingId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.LeadingBidderId.ShouldBe(TestData.BidderId);
    }

    [Fact]
    public async Task Buyout_without_a_configured_price_is_refused()
    {
        var (listingId, _) = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBuyoutAsync(listingId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.no_buyout");
    }

    [Fact]
    public async Task A_seller_cannot_buy_out_their_own_lot()
    {
        var (listingId, _) = Seed(buyoutPrice: Money.FromRupiah(3_000_000));
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostBuyoutAsync(listingId);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.seller_cannot_bid");
    }

    /// <summary>Seeds a live auction listing and returns its id alongside the listing.</summary>
    private (string ListingId, Listing Listing) Seed(Money? reservePrice = null, Money? buyoutPrice = null)
    {
        var listing = TestData.ActiveListing(
            price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);

        var auction = TestData.LiveAuction(
            listing, Money.FromRupiah(1_000_000), reservePrice: reservePrice, buyoutPrice: buyoutPrice);
        auction.Status.ShouldBe(AuctionStatus.Live);

        factory.Listings.Seed(listing);
        factory.Auctions.Seed(auction);

        return (listing.Id, listing);
    }
}
