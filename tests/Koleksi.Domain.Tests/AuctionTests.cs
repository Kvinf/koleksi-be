using Koleksi.Domain.Auctions;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class AuctionTests
{
    private const string Seller = "seller-sub";
    private const string Alice = "alice-sub";
    private const string Bob = "bob-sub";

    private static (Auction Auction, TestClock Clock) LiveAuction(
        long startingBid = 1_000_000, long? reservePrice = null, long? buyoutPrice = null)
    {
        var clock = TestClock.AtEpoch();
        var auction = Auction.Create(
            EntityId.New(clock.UtcNow),
            Seller,
            Money.FromRupiah(startingBid),
            TimeSpan.FromDays(3),
            clock,
            reservePrice is { } reserve ? Money.FromRupiah(reserve) : null,
            buyoutPrice is { } buyout ? Money.FromRupiah(buyout) : null);
        auction.Open(clock);
        return (auction, clock);
    }

    [Fact]
    public void Opening_bid_settles_at_exactly_the_amount_bid()
    {
        var (auction, clock) = LiveAuction();

        var outcome = auction.PlaceBid(Alice, "alice", Money.FromRupiah(5_000_000), clock);

        outcome.IsLeading.ShouldBeTrue();
        auction.CurrentPrice.ShouldBe(Money.FromRupiah(5_000_000));
        auction.LeadingBidderId.ShouldBe(Alice);
    }

    [Fact]
    public void A_higher_bid_takes_the_lead_at_exactly_the_amount_bid()
    {
        var (auction, clock) = LiveAuction();
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        var outcome = auction.PlaceBid(Bob, "bob", Money.FromRupiah(3_000_000), clock);

        outcome.IsLeading.ShouldBeTrue();
        outcome.OutbidUserId.ShouldBe(Alice);
        auction.LeadingBidderId.ShouldBe(Bob);
        auction.CurrentPrice.ShouldBe(Money.FromRupiah(3_000_000));

        // Exactly one row — no automatic counter-bid on anyone's behalf.
        outcome.Bids.ShouldHaveSingleItem();
        outcome.Bids[0].BidderId.ShouldBe(Bob);
        outcome.Bids[0].Amount.ShouldBe(Money.FromRupiah(3_000_000));
    }

    [Fact]
    public void A_leading_bidder_cannot_bid_again_while_still_leading()
    {
        var (auction, clock) = LiveAuction();
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        Should.Throw<DomainException>(() => auction.PlaceBid(Alice, "alice", Money.FromRupiah(3_000_000), clock))
            .Code.ShouldBe("auction.already_leading");
    }

    [Fact]
    public void A_bid_below_the_next_minimum_is_rejected()
    {
        var (auction, clock) = LiveAuction();
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(1_000_000), clock);

        Should.Throw<DomainException>(() => auction.PlaceBid(Bob, "bob", Money.FromRupiah(1_010_000), clock))
            .Code.ShouldBe("auction.bid_too_low");
    }

    [Fact]
    public void The_seller_cannot_bid_on_their_own_lot() =>
        Should.Throw<DomainException>(() =>
        {
            var (auction, clock) = LiveAuction();
            auction.PlaceBid(Seller, "seller", Money.FromRupiah(2_000_000), clock);
        }).Code.ShouldBe("auction.seller_cannot_bid");

    [Fact]
    public void A_bid_inside_the_final_window_extends_the_close_time()
    {
        var (auction, clock) = LiveAuction();
        clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromSeconds(20));

        var outcome = auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        outcome.WasExtended.ShouldBeTrue();
        auction.EndsAt.ShouldBe(clock.UtcNow + Auction.AntiSnipeWindow);
    }

    [Fact]
    public void A_bid_well_before_the_end_does_not_extend_the_close_time()
    {
        var (auction, clock) = LiveAuction();
        var originalEnd = auction.EndsAt;

        var outcome = auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        outcome.WasExtended.ShouldBeFalse();
        auction.EndsAt.ShouldBe(originalEnd);
    }

    [Fact]
    public void Bidding_after_the_end_time_is_rejected()
    {
        var (auction, clock) = LiveAuction();
        clock.Advance(TimeSpan.FromDays(4));

        Should.Throw<DomainException>(() => auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock))
            .Code.ShouldBe("auction.ended");
    }

    [Fact]
    public void Closing_with_no_bids_ends_without_a_sale()
    {
        var (auction, clock) = LiveAuction();
        clock.Advance(TimeSpan.FromDays(4));

        auction.Close(clock);

        auction.Status.ShouldBe(AuctionStatus.EndedWithoutSale);
    }

    [Fact]
    public void Closing_with_bids_ends_awaiting_settlement()
    {
        var (auction, clock) = LiveAuction();
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);
        clock.Advance(TimeSpan.FromDays(4));

        auction.Close(clock);

        auction.Status.ShouldBe(AuctionStatus.Ended);
        auction.LeadingBidderId.ShouldBe(Alice);
    }

    [Fact]
    public void An_auction_with_bids_cannot_be_cancelled_by_the_seller()
    {
        var (auction, clock) = LiveAuction();
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        Should.Throw<DomainException>(() => auction.Cancel(Seller)).Code.ShouldBe("auction.has_bids");
    }

    [Theory]
    [InlineData(50_000, 5_000)]
    [InlineData(300_000, 10_000)]
    [InlineData(750_000, 25_000)]
    [InlineData(3_000_000, 50_000)]
    [InlineData(12_000_000, 100_000)]
    [InlineData(40_000_000, 250_000)]
    public void The_increment_ladder_scales_with_the_current_price(long price, long expectedIncrement)
    {
        var auction = Auction.Rehydrate(
            "L1", Seller, Money.FromRupiah(price), Money.FromRupiah(price), 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), AuctionStatus.Live,
            Alice, "alice", 1);

        auction.MinimumIncrement.ShouldBe(Money.FromRupiah(expectedIncrement));
    }

    [Fact]
    public void Every_bid_increments_the_concurrency_version()
    {
        var (auction, clock) = LiveAuction();
        var before = auction.Version;

        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);

        auction.Version.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void Closing_below_reserve_ends_without_a_sale_even_with_bids()
    {
        var (auction, clock) = LiveAuction(reservePrice: 5_000_000);
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);
        clock.Advance(TimeSpan.FromDays(4));

        auction.ReserveMet.ShouldBeFalse();
        auction.Close(clock);

        auction.Status.ShouldBe(AuctionStatus.EndedWithoutSale);
    }

    [Fact]
    public void Closing_at_or_above_reserve_ends_awaiting_settlement()
    {
        var (auction, clock) = LiveAuction(reservePrice: 2_000_000);
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(2_000_000), clock);
        clock.Advance(TimeSpan.FromDays(4));

        auction.ReserveMet.ShouldBeTrue();
        auction.Close(clock);

        auction.Status.ShouldBe(AuctionStatus.Ended);
    }

    [Fact]
    public void A_reserve_below_the_starting_bid_is_rejected_at_creation()
    {
        var clock = TestClock.AtEpoch();

        Should.Throw<DomainException>(() => Auction.Create(
                "L1", Seller, Money.FromRupiah(1_000_000), TimeSpan.FromDays(1), clock,
                reservePrice: Money.FromRupiah(500_000)))
            .Code.ShouldBe("auction.invalid_reserve");
    }

    [Fact]
    public void A_buyout_at_or_below_the_starting_bid_is_rejected_at_creation()
    {
        var clock = TestClock.AtEpoch();

        Should.Throw<DomainException>(() => Auction.Create(
                "L1", Seller, Money.FromRupiah(1_000_000), TimeSpan.FromDays(1), clock,
                buyoutPrice: Money.FromRupiah(1_000_000)))
            .Code.ShouldBe("auction.invalid_buyout");
    }

    [Fact]
    public void Buyout_ends_the_auction_immediately_at_the_buyout_price()
    {
        var (auction, clock) = LiveAuction(buyoutPrice: 3_000_000);
        auction.PlaceBid(Alice, "alice", Money.FromRupiah(1_100_000), clock);

        var outcome = auction.Buyout(Bob, "bob", clock);

        auction.Status.ShouldBe(AuctionStatus.Ended);
        auction.CurrentPrice.ShouldBe(Money.FromRupiah(3_000_000));
        auction.LeadingBidderId.ShouldBe(Bob);
        outcome.OutbidUserId.ShouldBe(Alice);
    }

    [Fact]
    public void Buyout_without_a_configured_price_is_rejected()
    {
        var (auction, clock) = LiveAuction();

        Should.Throw<DomainException>(() => auction.Buyout(Bob, "bob", clock))
            .Code.ShouldBe("auction.no_buyout");
    }

    [Fact]
    public void The_seller_cannot_buy_out_their_own_lot()
    {
        var (auction, clock) = LiveAuction(buyoutPrice: 3_000_000);

        Should.Throw<DomainException>(() => auction.Buyout(Seller, "seller", clock))
            .Code.ShouldBe("auction.seller_cannot_bid");
    }
}

public sealed class BidderAliasTests
{
    [Theory]
    [InlineData("randisetiawanhw", "ran•••hw")]
    [InlineData("slabvault", "sla•••lt")]
    [InlineData("abcd", "a•••")]
    [InlineData("", "•••")]
    public void Masks_handles_for_the_public_history(string input, string expected) =>
        BidderAlias.Mask(input).ShouldBe(expected);
}
