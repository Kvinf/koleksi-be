using System.Text.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Application.Features.Auctions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Standing;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The scheduled duties, against in-memory storage and a clock the test moves.
/// </summary>
/// <remarks>
/// Handlers are built directly rather than through the host: there is no caller, only a clock,
/// and a test host would add a shared clock and shared fakes that one test's closed auctions
/// would leak into the next one's counts.
/// </remarks>
public sealed class SweepHandlerTests
{
    // ─── Closing auctions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_auctions_past_their_end_time_are_closed()
    {
        var h = new Harness();
        var finished = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        var running = h.SeedAuction(TimeSpan.FromDays(3), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));

        var result = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Sold.ShouldBe(1);
        (await h.Auctions.FindAsync(finished.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Ended);
        (await h.Auctions.FindAsync(running.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Live);
    }

    [Fact]
    public async Task An_auction_that_met_its_reserve_ends_with_a_winner()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 2_000_000, reserve: Money.FromRupiah(2_000_000));
        h.Clock.Advance(TimeSpan.FromHours(2));

        var result = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Sold.ShouldBe(1);
        var closed = (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!;
        closed.Status.ShouldBe(AuctionStatus.Ended);
        closed.LeadingBidderId.ShouldBe(TestData.BidderId);
    }

    [Fact]
    public async Task An_auction_below_its_reserve_ends_without_a_sale()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_500_000, reserve: Money.FromRupiah(3_000_000));
        h.Clock.Advance(TimeSpan.FromHours(2));

        var result = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Sold.ShouldBe(0);
        result.Unsold.ShouldBe(1);
        (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.EndedWithoutSale);
    }

    [Fact]
    public async Task An_auction_with_no_bids_ends_without_a_sale()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: null);
        h.Clock.Advance(TimeSpan.FromHours(2));

        var result = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Unsold.ShouldBe(1);
        (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.EndedWithoutSale);
    }

    [Fact]
    public async Task A_second_run_finds_nothing_left_to_do()
    {
        var h = new Harness();
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));

        await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);
        var pushedAfterFirst = h.Realtime.Published.Count;
        var second = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        second.ShouldBe(new CloseDueAuctionsResult(0, 0, 0, 0));
        h.Realtime.Published.Count.ShouldBe(pushedAfterFirst);
    }

    [Fact]
    public async Task A_stale_index_entry_for_an_auction_that_is_already_closed_is_skipped_not_closed_twice()
    {
        // The due list is eventually consistent. Hand the handler an auction the consistent read
        // says is already over: it must not close it again or push again.
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));
        var stale = new StaleDueAuctions(h.Auctions, lot.Auction);
        await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);
        var pushed = h.Realtime.Published.Count;

        var handler = new CloseDueAuctionsHandler(
            stale, h.Announcer, h.Clock, NullLogger<CloseDueAuctionsHandler>.Instance);
        var result = await handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Skipped.ShouldBe(1);
        result.Sold.ShouldBe(0);
        h.Realtime.Published.Count.ShouldBe(pushed);
    }

    [Fact]
    public async Task A_bid_that_wins_the_race_makes_the_close_conflict_and_the_auction_waits_for_the_next_run()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));
        h.Auctions.ConflictsToInject = 1;

        var first = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        first.Skipped.ShouldBe(1);
        first.Errors.ShouldBe(0);
        (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Live);
        h.Realtime.Published.ShouldBeEmpty();

        var second = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        second.Sold.ShouldBe(1);
    }

    [Fact]
    public async Task One_run_closes_at_most_the_batch_size_and_the_rest_wait()
    {
        var h = new Harness();
        for (var i = 0; i < 7; i++)
        {
            h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        }

        h.Clock.Advance(TimeSpan.FromHours(2));

        (await h.Close.RunAsync(new SweepBudget(maxItemsPerDuty: 3), CancellationToken.None)).Sold.ShouldBe(3);
        (await h.Close.RunAsync(new SweepBudget(maxItemsPerDuty: 3), CancellationToken.None)).Sold.ShouldBe(3);
        (await h.Close.RunAsync(new SweepBudget(maxItemsPerDuty: 3), CancellationToken.None)).Sold.ShouldBe(1);
        (await h.Close.RunAsync(new SweepBudget(maxItemsPerDuty: 3), CancellationToken.None)).Sold.ShouldBe(0);
    }

    [Fact]
    public async Task No_new_work_is_started_once_the_time_allowance_is_spent()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));

        var result = await h.Close.RunAsync(new SweepBudget(timeLimit: TimeSpan.Zero), CancellationToken.None);

        result.Sold.ShouldBe(0);
        (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Live);
    }

    [Fact]
    public async Task A_failing_push_or_browse_removal_does_not_fail_the_sweep_or_undo_the_close()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));
        h.Realtime.FailWith = new InvalidOperationException("socket api down");
        h.SearchIndex.FailWith = new InvalidOperationException("index down");

        var result = await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Sold.ShouldBe(1);
        result.Errors.ShouldBe(0);
        (await h.Auctions.FindAsync(lot.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Ended);
    }

    [Fact]
    public async Task A_sold_auction_leaves_browse_and_tells_the_page_the_winner_and_the_seller()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));

        await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        h.SearchIndex.Removed.ShouldContain(lot.Id);

        var closed = h.Realtime.Published.Single(p => p.Topic == RealtimeTopics.Auction(lot.Id));
        closed.Type.ShouldBe("closed");
        var public_ = closed.Data.ShouldBeOfType<AuctionClosedEvent>();
        public_.Status.ShouldBe("Ended");
        public_.CurrentPriceRupiah.ShouldBe(1_200_000);

        var won = h.Realtime.Published.Single(p => p.Topic == RealtimeTopics.User(TestData.BidderId));
        won.Type.ShouldBe("auction-won");
        var notice = won.Data.ShouldBeOfType<AuctionResultEvent>();
        notice.ListingId.ShouldBe(lot.Id);
        notice.Title.ShouldBe(lot.Listing.Title);
        notice.AmountRupiah.ShouldBe(1_200_000);
        notice.Path.ShouldBe($"/lelang/{Slug.From(lot.Listing.Title)}-{lot.Id}");

        h.Realtime.Published.Single(p => p.Topic == RealtimeTopics.User(TestData.SellerId)).Type.ShouldBe("auction-sold");
    }

    [Fact]
    public async Task What_goes_to_the_public_topic_and_to_the_seller_never_names_the_winner()
    {
        var h = new Harness();
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));

        await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        foreach (var pushed in h.Realtime.Published.Where(p => p.Topic != RealtimeTopics.User(TestData.BidderId)))
        {
            var wire = JsonSerializer.Serialize(pushed.Data);
            wire.ShouldNotContain(TestData.BidderId);
            wire.ShouldNotContain(TestData.BidderUsername);
        }
    }

    [Fact]
    public async Task An_unsold_auction_leaves_browse_and_tells_only_the_page()
    {
        var h = new Harness();
        var lot = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: null);
        h.Clock.Advance(TimeSpan.FromHours(2));

        await h.Close.RunAsync(new SweepBudget(), CancellationToken.None);

        h.SearchIndex.Removed.ShouldContain(lot.Id);
        var only = h.Realtime.Published.ShouldHaveSingleItem();
        only.Topic.ShouldBe(RealtimeTopics.Auction(lot.Id));
        only.Type.ShouldBe("closed");
        only.Data.ShouldBeOfType<AuctionClosedEvent>().Status.ShouldBe("EndedWithoutSale");
    }

    // ─── Cancelling unpaid orders ─────────────────────────────────────────────────

    [Fact]
    public async Task Only_unpaid_orders_past_their_window_are_cancelled()
    {
        var h = new Harness();
        var lapsed = h.SeedOrder();
        var paid = h.SeedOrder(pay: true);
        h.Clock.Advance(Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1));
        var fresh = h.SeedOrder();

        var result = await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Cancelled.ShouldBe(1);
        (await h.Orders.FindAsync(lapsed.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Cancelled);
        (await h.Orders.FindAsync(paid.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);
        (await h.Orders.FindAsync(fresh.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public async Task An_unpaid_order_is_left_alone_for_a_grace_period_after_its_window_closes_so_a_late_payment_can_still_land()
    {
        var h = new Harness();
        var order = h.SeedOrder();

        // The window has closed but the grace has not elapsed: a buyer paying in the last moments, or a
        // delayed notification, must still find the order payable.
        h.Clock.Advance(Order.PaymentWindow + TimeSpan.FromSeconds(1));
        var early = await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);
        early.Cancelled.ShouldBe(0);
        (await h.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.AwaitingPayment);

        h.Clock.Advance(CancelUnpaidOrdersHandler.Grace);
        var late = await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);
        late.Cancelled.ShouldBe(1);
    }

    [Fact]
    public async Task Cancelling_an_order_frees_the_listing_it_was_holding()
    {
        var h = new Harness();
        // A hold taken well after the order was created (a retried checkout, say) outlives the order's
        // own window and its grace, so without the explicit release the item would stay unavailable.
        var order = h.SeedOrder();
        var line = order.Lines[0];
        h.Clock.Advance(TimeSpan.FromMinutes(20));
        (await h.Holds.TryHoldAsync(line.ListingId, order.BuyerId, order.Id, CancellationToken.None)).Acquired.ShouldBeTrue();

        // 26 minutes in: past the window plus grace (25), while the hold (until 35) is still live.
        h.Clock.Advance(TimeSpan.FromMinutes(6));

        var before = await h.Holds.TryHoldAsync(line.ListingId, TestData.RivalId, "other-order", CancellationToken.None);
        before.Acquired.ShouldBeFalse();

        await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);

        var after = await h.Holds.TryHoldAsync(line.ListingId, TestData.RivalId, "other-order", CancellationToken.None);
        after.Acquired.ShouldBeTrue();
    }

    [Fact]
    public async Task A_second_cancellation_run_does_nothing()
    {
        var h = new Harness();
        h.SeedOrder();
        h.Clock.Advance(Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1));

        await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);
        var second = await h.Cancel.RunAsync(new SweepBudget(), CancellationToken.None);

        second.ShouldBe(new CancelUnpaidOrdersResult(0, 0, 0));
    }

    [Fact]
    public async Task A_payment_that_lands_first_wins_and_the_order_is_not_cancelled()
    {
        var h = new Harness();
        var order = h.SeedOrder();
        h.Clock.Advance(Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1));

        // The buyer's payment is recorded between the sweep reading the order and writing it.
        var racing = new RacingOrders(h.Orders, onBeforeUpdate: async () =>
        {
            var current = (await h.Orders.FindAsync(order.Id, CancellationToken.None))!;
            var version = current.Version;
            current.ConfirmPayment(current.GrandTotal, h.Clock);
            await h.Orders.UpdateAsync(current, version, CancellationToken.None);
        });
        var handler = new CancelUnpaidOrdersHandler(
            racing, h.Holds, h.Standing, h.Clock, NullLogger<CancelUnpaidOrdersHandler>.Instance);

        var result = await handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Cancelled.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        (await h.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task One_run_cancels_at_most_the_batch_size()
    {
        var h = new Harness();
        for (var i = 0; i < 5; i++)
        {
            h.SeedOrder();
        }

        h.Clock.Advance(Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1));

        (await h.Cancel.RunAsync(new SweepBudget(maxItemsPerDuty: 2), CancellationToken.None)).Cancelled.ShouldBe(2);
        (await h.Cancel.RunAsync(new SweepBudget(maxItemsPerDuty: 2), CancellationToken.None)).Cancelled.ShouldBe(2);
        (await h.Cancel.RunAsync(new SweepBudget(maxItemsPerDuty: 2), CancellationToken.None)).Cancelled.ShouldBe(1);
    }

    // ─── Releasing escrow ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_delivered_orders_past_the_confirmation_window_are_released()
    {
        var h = new Harness();
        var due = h.SeedDelivered();
        h.Clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));
        var notYet = h.SeedDelivered();
        var shipped = h.SeedOrder(pay: true);

        var result = await h.Release.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Released.ShouldBe(1);
        (await h.Orders.FindAsync(due.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Released);
        (await h.Orders.FindAsync(notYet.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Delivered);
        (await h.Orders.FindAsync(shipped.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task An_order_the_buyer_disputed_is_never_released()
    {
        var h = new Harness();
        var order = h.SeedDelivered();
        var current = (await h.Orders.FindAsync(order.Id, CancellationToken.None))!;
        var version = current.Version;
        current.OpenDispute("Barang tidak sesuai", current.BuyerId, h.Clock);
        await h.Orders.UpdateAsync(current, version, CancellationToken.None);
        h.Clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));

        var result = await h.Release.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Released.ShouldBe(0);
        (await h.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Disputed);
    }

    [Fact]
    public async Task A_dispute_opened_after_the_sweep_read_the_order_stops_the_release()
    {
        var h = new Harness();
        var order = h.SeedDelivered();
        h.Clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));

        var racing = new RacingOrders(h.Orders, onBeforeUpdate: async () =>
        {
            var current = (await h.Orders.FindAsync(order.Id, CancellationToken.None))!;
            var version = current.Version;
            current.OpenDispute("Barang tidak sesuai", current.BuyerId, h.Clock);
            await h.Orders.UpdateAsync(current, version, CancellationToken.None);
        });
        var handler = new ReleaseDueEscrowHandler(
            racing, new EscrowReleaseService(racing, h.Sender, h.Clock), h.Clock, NullLogger<ReleaseDueEscrowHandler>.Instance);

        var result = await handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Released.ShouldBe(0);
        result.Skipped.ShouldBe(1);
        (await h.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Disputed);
    }

    [Fact]
    public async Task A_second_release_run_does_nothing()
    {
        var h = new Harness();
        h.SeedDelivered();
        h.Clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));

        await h.Release.RunAsync(new SweepBudget(), CancellationToken.None);
        var second = await h.Release.RunAsync(new SweepBudget(), CancellationToken.None);

        second.ShouldBe(new ReleaseDueEscrowResult(0, 0, 0));
    }

    // ─── The orchestrator ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_orchestrator_runs_every_duty_and_totals_them()
    {
        var h = new Harness();
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: null);
        h.SeedOrder();
        h.SeedDelivered();
        h.Clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));

        var summary = await h.Orchestrator.RunAsync(CancellationToken.None);

        summary.ClosedAuctions.ShouldBe(1);
        summary.EndedWithoutSale.ShouldBe(1);
        summary.CancelledOrders.ShouldBe(1);
        summary.ReleasedOrders.ShouldBe(1);
        summary.Errors.ShouldBe(0);
        summary.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task One_duty_failing_does_not_stop_the_others()
    {
        var h = new Harness();
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.SeedOrder();
        h.Clock.Advance(Order.PaymentWindow + TimeSpan.FromHours(2));
        h.Auctions.FailWith = new InvalidOperationException("table throttled");

        var summary = await h.Orchestrator.RunAsync(CancellationToken.None);

        summary.ClosedAuctions.ShouldBe(0);
        summary.CancelledOrders.ShouldBe(1);

        // Two duties read the auction table (closing auctions, and warning winners who never paid), so a
        // throttled table fails both; the order duties still ran.
        summary.Errors.ShouldBe(2);
    }

    [Fact]
    public async Task A_poisoned_item_is_counted_and_the_rest_of_the_batch_still_runs()
    {
        var h = new Harness();
        h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        var second = h.SeedAuction(TimeSpan.FromHours(1), bidRupiah: 1_200_000);
        h.Clock.Advance(TimeSpan.FromHours(2));
        var failing = new FailingForListing(h.Auctions, failFor: second.Id);

        var handler = new CloseDueAuctionsHandler(
            failing, h.Announcer, h.Clock, NullLogger<CloseDueAuctionsHandler>.Instance);
        var result = await handler.RunAsync(new SweepBudget(), CancellationToken.None);

        result.Errors.ShouldBe(1);
        result.Sold.ShouldBe(1);
    }

    // ─── Harness ──────────────────────────────────────────────────────────────────

    private sealed record Lot(Listing Listing, Auction Auction)
    {
        public string Id => Listing.Id;
    }

    private sealed class Harness
    {
        public Harness() => Holds = new FakeListingHoldStore(Clock);

        public MutableClock Clock { get; } = new();

        public FakeAuctionRepository Auctions { get; } = new();

        public FakeListingRepository Listings { get; } = new();

        public FakeListingSearchIndex SearchIndex { get; } = new();

        public FakeRealtimeBroadcaster Realtime { get; } = new();

        public FakeOrderRepository Orders { get; } = new();

        public FakeListingHoldStore Holds { get; }

        public AuctionCloseAnnouncer Announcer => new(
            Listings, SearchIndex, Realtime, NullLogger<AuctionCloseAnnouncer>.Instance);

        public CloseDueAuctionsHandler Close => new(
            Auctions, Announcer, Clock, NullLogger<CloseDueAuctionsHandler>.Instance);

        public FakeAccountStandingRepository Standings { get; } = new();

        public FakeProfileRepository Profiles { get; } = new();

        public FakeDeliveryTracker Courier { get; } = new();

        public FakeEscrowPayout PayoutProvider { get; } = new();

        public AccountStandingService Standing => new(
            Standings, Listings, SearchIndex, new BanStatusCache(Clock), Clock,
            NullLogger<AccountStandingService>.Instance);

        public PayoutSender Sender => new(
            Orders.Payouts, Profiles, PayoutProvider, Clock, NullLogger<PayoutSender>.Instance);

        public EscrowReleaseService ReleaseService => new(Orders, Sender, Clock);

        public CancelUnpaidOrdersHandler Cancel => new(
            Orders, Holds, Standing, Clock, NullLogger<CancelUnpaidOrdersHandler>.Instance);

        public ReleaseDueEscrowHandler Release => new(
            Orders, ReleaseService, Clock, NullLogger<ReleaseDueEscrowHandler>.Instance);

        public TrackShipmentsHandler Track => new(
            Orders, Courier, Clock, NullLogger<TrackShipmentsHandler>.Instance);

        public SendPendingPayoutsHandler SendPayouts => new(
            Orders.Payouts, Sender, Clock, NullLogger<SendPendingPayoutsHandler>.Instance);

        public ExpireAuctionPaymentsHandler ExpireAuctionPayments => new(
            Auctions, Listings, Standing, Clock, NullLogger<ExpireAuctionPaymentsHandler>.Instance);

        public SweepOrchestrator Orchestrator => new(
            Close, Track, Release, SendPayouts, ExpireAuctionPayments, Cancel, NullLogger<SweepOrchestrator>.Instance);

        /// <summary>A live auction that started now and runs for <paramref name="duration"/>.</summary>
        public Lot SeedAuction(TimeSpan duration, long? bidRupiah, Money? reserve = null)
        {
            var listing = TestData.ActiveListing(
                title: $"Lelang Kartu Langka Nomor {Guid.NewGuid():N}"[..40],
                price: Money.FromRupiah(1_000_000),
                format: ListingFormat.Auction);

            var auction = TestData.LiveAuction(listing, Money.FromRupiah(1_000_000), duration, reserve);
            if (bidRupiah is { } amount)
            {
                auction.PlaceBid(TestData.BidderId, TestData.BidderUsername, Money.FromRupiah(amount), Clock);
            }

            Listings.Seed(listing);
            Auctions.Seed(auction);
            return new Lot(listing, auction);
        }

        /// <summary>An order created at the harness clock's current instant.</summary>
        public Order SeedOrder(bool pay = false)
        {
            var listing = TestData.ActiveListing(title: $"Kartu Dijual Nomor {Guid.NewGuid():N}"[..36]);
            Listings.Seed(listing);

            var order = Order.Create(
                TestData.BidderId,
                listing.SellerId,
                [new OrderLine(listing.Id, listing.Title, null, listing.Price, 1)],
                new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
                new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
                Clock);

            if (pay)
            {
                order.ConfirmPayment(order.GrandTotal, Clock);
            }

            Orders.Seed(order);
            return order;
        }

        /// <summary>A paid, shipped order that was marked delivered at the harness clock's current instant.</summary>
        public Order SeedDelivered()
        {
            var order = SeedOrder(pay: true);
            order.MarkShipped(
                Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 3, 1)),
                order.SellerId,
                Clock);
            order.MarkDelivered(Clock);
            Orders.Seed(order);
            return order;
        }
    }

    /// <summary>Reports a given auction as due although its stored state is already closed.</summary>
    private sealed class StaleDueAuctions(FakeAuctionRepository inner, Auction stale) : IAuctionRepository
    {
        public Task<Auction?> FindAsync(string listingId, CancellationToken ct) => inner.FindAsync(listingId, ct);

        public Task AddAsync(Auction auction, CancellationToken ct) => inner.AddAsync(auction, ct);

        public Task SaveBidAsync(Auction auction, IReadOnlyList<Bid> newBids, long expectedVersion, CancellationToken ct) =>
            inner.SaveBidAsync(auction, newBids, expectedVersion, ct);

        public Task<IReadOnlyList<Bid>> GetBidHistoryAsync(string listingId, int limit, CancellationToken ct) =>
            inner.GetBidHistoryAsync(listingId, limit, ct);

        public Task<IReadOnlyList<Auction>> ListDueForCloseAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Auction>>([stale]);

        public Task<IReadOnlyList<Auction>> ListDueForWinnerPaymentAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListDueForWinnerPaymentAsync(asOf, limit, ct);
    }

    /// <summary>Throws from the consistent read of one auction, to stand for a corrupt or throttled row.</summary>
    private sealed class FailingForListing(FakeAuctionRepository inner, string failFor) : IAuctionRepository
    {
        public Task<Auction?> FindAsync(string listingId, CancellationToken ct) =>
            listingId == failFor ? throw new InvalidOperationException("row cannot be read") : inner.FindAsync(listingId, ct);

        public Task AddAsync(Auction auction, CancellationToken ct) => inner.AddAsync(auction, ct);

        public Task SaveBidAsync(Auction auction, IReadOnlyList<Bid> newBids, long expectedVersion, CancellationToken ct) =>
            inner.SaveBidAsync(auction, newBids, expectedVersion, ct);

        public Task<IReadOnlyList<Bid>> GetBidHistoryAsync(string listingId, int limit, CancellationToken ct) =>
            inner.GetBidHistoryAsync(listingId, limit, ct);

        public Task<IReadOnlyList<Auction>> ListDueForCloseAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListDueForCloseAsync(asOf, limit, ct);

        public Task<IReadOnlyList<Auction>> ListDueForWinnerPaymentAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListDueForWinnerPaymentAsync(asOf, limit, ct);
    }

    /// <summary>Runs a hook just before the sweep's write, so a rival write can land first.</summary>
    private sealed class RacingOrders(FakeOrderRepository inner, Func<Task> onBeforeUpdate) : IOrderRepository
    {
        private bool _raced;

        public Task<Order?> FindAsync(string orderId, CancellationToken ct) => inner.FindAsync(orderId, ct);

        public Task<Order?> FindByOrderNumberAsync(string orderNumber, CancellationToken ct) =>
            inner.FindByOrderNumberAsync(orderNumber, ct);

        public Task AddManyAsync(IReadOnlyList<Order> orders, CancellationToken ct) => inner.AddManyAsync(orders, ct);

        public async Task UpdateAsync(Order order, long expectedVersion, CancellationToken ct)
        {
            if (!_raced)
            {
                _raced = true;
                await onBeforeUpdate().ConfigureAwait(false);
            }

            await inner.UpdateAsync(order, expectedVersion, ct).ConfigureAwait(false);
        }

        public Task<Page<Order>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct) =>
            inner.ListForBuyerAsync(buyerId, cursor, limit, ct);

        public Task<Page<Order>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct) =>
            inner.ListForSellerAsync(sellerId, cursor, limit, ct);

        public Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListDueForAutoReleaseAsync(asOf, limit, ct);

        public Task<IReadOnlyList<Order>> ListPaymentExpiredAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListPaymentExpiredAsync(asOf, limit, ct);

        public Task<IReadOnlyList<Order>> ListDueForTrackingAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
            inner.ListDueForTrackingAsync(asOf, limit, ct);

        public async Task UpdateWithPayoutAsync(Order order, long expectedVersion, Payout payout, CancellationToken ct)
        {
            if (!_raced)
            {
                _raced = true;
                await onBeforeUpdate().ConfigureAwait(false);
            }

            await inner.UpdateWithPayoutAsync(order, expectedVersion, payout, ct).ConfigureAwait(false);
        }
    }
}
