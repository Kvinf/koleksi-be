using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Standing;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The owner's two promises end to end, through the real handlers and sweeps on in-memory storage:
/// money is released two days after delivery (or when the buyer confirms) and the seller is paid; and a broken
/// commitment is a warning, three warnings are a ban, and a clean month forgives one.
/// </summary>
public sealed class EscrowAndStandingTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), EstimatedDays: 3);

    private static readonly ShippingAddress Address =
        new("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810");

    private static string NewId() => Guid.NewGuid().ToString();

    private Order NewOrder(
        string buyerId,
        string sellerId,
        OrderSource source = OrderSource.FixedPrice,
        string? reference = null,
        DateTimeOffset? due = null,
        string? listingId = null)
    {
        var order = Order.Create(
            buyerId,
            sellerId,
            [new OrderLine(listingId ?? NewId(), "Kartu uji", null, Money.FromRupiah(1_000_000), 1)],
            Address,
            Jne,
            factory.Clock,
            source,
            due,
            reference);
        factory.Orders.Seed(order);
        return order;
    }

    private Order ShippedOrder(string buyerId, string sellerId)
    {
        var order = NewOrder(buyerId, sellerId);
        order.ConfirmPayment(order.GrandTotal, factory.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 3, 1)),
            sellerId,
            factory.Clock);
        factory.Orders.Seed(order);
        return order;
    }

    private async Task<SweepSummary> SweepAsync()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SweepOrchestrator>().RunAsync(CancellationToken.None);
    }

    private async Task<int> WarningsOf(string userId) =>
        (await factory.Standings.FindAsync(userId, CancellationToken.None))?.WarningsAt(factory.Clock.UtcNow) ?? 0;

    // The factory is shared by every test in this class, so each test looks only at its own order's payout.
    private List<Payout> PayoutsOf(Order order) => factory.Payouts.All.Where(p => p.OrderId == order.Id).ToList();

    private List<(string OrderId, long AmountRupiah, BankAccount? Destination)> SentFor(Order order) =>
        factory.PayoutProvider.Sent.Where(s => s.OrderId == order.Id).ToList();

    private async Task SeedBankAccountAsync(string sellerId)
    {
        var profile = UserProfile.CreateEmpty(sellerId, factory.Clock);
        profile.SetBankAccount(BankAccount.Create("BCA", "1234567890", "Penjual Uji"), factory.Clock);
        await factory.Profiles.AddAsync(profile, CancellationToken.None);
    }

    private (Listing Listing, Auction Auction) EndedAuction(string winnerId)
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);
        factory.Listings.Seed(listing);
        var auction = TestData.LiveAuction(listing, Money.FromRupiah(1_000_000));
        auction.PlaceBid(winnerId, "winner", Money.FromRupiah(1_100_000), factory.Clock);
        factory.Clock.UtcNow = auction.EndsAt + TimeSpan.FromSeconds(1);
        auction.Close(factory.Clock);
        factory.Auctions.Seed(auction);
        return (listing, auction);
    }

    private async Task WithClockAsync(DateTimeOffset at, Func<Task> body)
    {
        factory.Clock.UtcNow = at;
        try
        {
            await body();
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    // ═════════ Warnings: what counts ═════════════════════════════════════════════════════════

    [Fact]
    public async Task An_auction_win_that_is_never_paid_earns_the_buyer_a_warning_when_its_thirty_minutes_lapse()
    {
        var buyer = NewId();
        var due = factory.Clock.UtcNow + Order.AuctionPaymentWindow;
        var order = NewOrder(buyer, NewId(), OrderSource.AuctionWin, $"L-{NewId()}", due);

        await WithClockAsync(due + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1), async () =>
        {
            await SweepAsync();

            (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Cancelled);
            (await WarningsOf(buyer)).ShouldBe(1);

            // A second run finds nothing more to do and does not warn twice.
            await SweepAsync();
            (await WarningsOf(buyer)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task A_nego_deal_that_is_never_paid_earns_a_warning()
    {
        var buyer = NewId();
        NewOrder(buyer, NewId(), OrderSource.AcceptedOffer, $"offer-{NewId()}");

        await WithClockAsync(
            factory.Clock.UtcNow + Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1),
            async () =>
            {
                await SweepAsync();
                (await WarningsOf(buyer)).ShouldBe(1);
            });
    }

    [Fact]
    public async Task A_plain_sale_that_is_never_paid_is_not_a_violation_and_just_frees_the_stock()
    {
        var buyer = NewId();
        var order = NewOrder(buyer, NewId());

        await WithClockAsync(
            factory.Clock.UtcNow + Order.PaymentWindow + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1),
            async () =>
            {
                await SweepAsync();

                (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Cancelled);
                (await factory.Standings.FindAsync(buyer, CancellationToken.None)).ShouldBeNull();
            });
    }

    [Fact]
    public async Task Cancelling_an_auction_or_nego_order_yourself_is_a_violation_but_cancelling_a_sale_is_not()
    {
        var buyer = NewId();
        var auctionOrder = NewOrder(buyer, NewId(), OrderSource.AuctionWin, $"L-{NewId()}", factory.Clock.UtcNow + Order.AuctionPaymentWindow);
        var saleOrder = NewOrder(buyer, NewId());
        using var client = factory.CreateClientAs(buyer, "canceller");

        (await client.PostAsync($"/api/orders/{saleOrder.Id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WarningsOf(buyer)).ShouldBe(0);

        (await client.PostAsync($"/api/orders/{auctionOrder.Id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WarningsOf(buyer)).ShouldBe(1);
    }

    [Fact]
    public async Task A_winner_who_never_opens_checkout_is_warned_once_thirty_minutes_after_the_close_and_the_lot_goes_back()
    {
        var winner = NewId();
        var (listing, auction) = EndedAuction(winner);
        var due = auction.PaymentDueAt!.Value;
        factory.Clock.UtcNow = MutableClock.Origin;

        // Still inside the thirty minutes (and the grace after): nothing happens.
        await WithClockAsync(due + CancelUnpaidOrdersHandler.Grace - TimeSpan.FromSeconds(1), async () =>
        {
            await SweepAsync();
            (await WarningsOf(winner)).ShouldBe(0);
        });

        await WithClockAsync(due + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1), async () =>
        {
            var summary = await SweepAsync();
            summary.LapsedAuctionPayments.ShouldBe(1);
            (await WarningsOf(winner)).ShouldBe(1);
            (await factory.Auctions.FindAsync(listing.Id, CancellationToken.None))!.WinnerPaymentResolvedAt.ShouldNotBeNull();

            await SweepAsync();
            (await WarningsOf(winner)).ShouldBe(1);

            // The lot is the seller's again.
            using var seller = factory.CreateClientAs(listing.SellerId, "seller.lapsed");
            (await seller.PostAsync($"/api/listings/{listing.Id}/archive", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        });
    }

    [Fact]
    public async Task A_winner_who_paid_is_not_warned()
    {
        var winner = NewId();
        var (listing, auction) = EndedAuction(winner);
        var paid = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        paid!.MarkSold(factory.Clock);
        await factory.Listings.UpdateAsync(paid, paid.Version - 1, CancellationToken.None);
        var due = auction.PaymentDueAt!.Value;

        await WithClockAsync(due + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromMinutes(5), async () =>
        {
            await SweepAsync();

            (await factory.Standings.FindAsync(winner, CancellationToken.None)).ShouldBeNull();
            (await factory.Auctions.FindAsync(listing.Id, CancellationToken.None))!.WinnerPaymentResolvedAt.ShouldNotBeNull();
        });
    }

    [Fact]
    public async Task One_broken_auction_is_one_warning_even_when_both_the_order_and_the_auction_sweep_find_it()
    {
        var winner = NewId();
        var (listing, auction) = EndedAuction(winner);
        var due = auction.PaymentDueAt!.Value;
        factory.Clock.UtcNow = MutableClock.Origin;

        // The winner opened checkout (an order for this very auction) and let it lapse too.
        NewOrder(winner, listing.SellerId, OrderSource.AuctionWin, listing.Id, due, listing.Id);

        await WithClockAsync(due + CancelUnpaidOrdersHandler.Grace + TimeSpan.FromSeconds(1), async () =>
        {
            await SweepAsync();
            (await WarningsOf(winner)).ShouldBe(1);
        });
    }

    // ═════════ Bans ══════════════════════════════════════════════════════════════════════════

    private async Task WarnAsync(string userId, int times)
    {
        using var scope = factory.Services.CreateScope();
        var standing = scope.ServiceProvider.GetRequiredService<AccountStandingService>();
        for (var i = 0; i < times; i++)
        {
            await standing.RecordViolationAsync(userId, ViolationType.AuctionWinNotPaid, $"auction:{NewId()}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_third_warning_bans_the_account_and_it_can_no_longer_start_anything_new()
    {
        var user = NewId();
        await WarnAsync(user, 3);
        using var client = factory.CreateClientAs(user, "banned.user");

        using var add = await client.PostAsJsonAsync("/api/cart/items", new { listingId = "anything" });
        add.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await add.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("account.banned");

        using var message = await client.PostAsJsonAsync("/api/conversations", new { listingId = "anything", text = "hi" });
        message.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var bid = await client.PostBidAsync("any-listing", 1_000_000);
        bid.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_banned_account_can_still_see_why_sign_out_and_finish_an_order_it_already_has()
    {
        var user = NewId();
        await WarnAsync(user, 3);
        using var client = factory.CreateClientAs(user, "banned.reader");

        using var profile = await client.GetAsync("/api/profile");
        profile.StatusCode.ShouldBe(HttpStatusCode.OK);
        var standing = (await profile.ReadJsonAsync()).GetProperty("standing");
        standing.GetProperty("banned").GetBoolean().ShouldBeTrue();
        standing.GetProperty("warnings").GetInt32().ShouldBe(3);
        standing.GetProperty("maxWarnings").GetInt32().ShouldBe(3);

        // An order the account already has is its own to finish: not refused as banned (it is simply not found).
        using var finish = await client.PostAsync($"/api/orders/{NewId()}/release", null);
        finish.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_banned_sellers_listings_leave_browse_and_nobody_can_check_out_from_them()
    {
        var seller = NewId();
        var listing = TestData.ActiveListingBy(seller);
        factory.Listings.Seed(listing);
        await WarnAsync(seller, 3);

        factory.SearchIndex.Removed.ShouldContain(listing.Id);

        using var buyer = factory.CreateClientAs(NewId(), "buys.from.banned");
        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", new
        {
            source = "FixedPrice",
            referenceId = listing.Id,
            shipTo = new
            {
                recipientName = "Rangga Wijaya", phone = "081234567890", street = "Jl. Merdeka No. 1",
                city = "Bandung", province = "Jawa Barat", postalCode = "40111",
            },
            courierCode = "jne",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("account.banned");
    }

    [Fact]
    public async Task The_profile_shows_how_many_warnings_an_account_has_and_when_one_is_forgiven()
    {
        var user = NewId();
        await WarnAsync(user, 2);
        using var client = factory.CreateClientAs(user, "warned.user");

        using var profile = await client.GetAsync("/api/profile");
        var standing = (await profile.ReadJsonAsync()).GetProperty("standing");

        standing.GetProperty("banned").GetBoolean().ShouldBeFalse();
        standing.GetProperty("warnings").GetInt32().ShouldBe(2);
        standing.GetProperty("nextReductionAt").GetDateTimeOffset().ShouldBe(factory.Clock.UtcNow + TimeSpan.FromDays(30));
    }

    // ═════════ Escrow release and payout ═════════════════════════════════════════════════════

    [Fact]
    public async Task A_delivered_parcel_is_released_two_days_later_and_the_seller_is_paid_the_items_less_the_fee()
    {
        var buyer = NewId();
        var seller = NewId();
        await SeedBankAccountAsync(seller);
        var order = ShippedOrder(buyer, seller);
        var shippedAt = factory.Clock.UtcNow;
        factory.Courier.Report = new DeliveryReport(DeliveryState.Delivered);

        // The courier is asked once its interval has passed, and reports it delivered.
        await WithClockAsync(shippedAt + Order.TrackingPollInterval, async () =>
        {
            var summary = await SweepAsync();
            summary.DeliveredOrders.ShouldBeGreaterThanOrEqualTo(1);
            var delivered = (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!;
            delivered.EscrowState.ShouldBe(EscrowState.Delivered);
            delivered.AutoReleaseAt.ShouldBe(factory.Clock.UtcNow + TimeSpan.FromDays(2));
        });

        var deliveredAt = shippedAt + Order.TrackingPollInterval;

        // One second short of two days: still the buyer's to confirm or dispute.
        await WithClockAsync(deliveredAt + TimeSpan.FromDays(2) - TimeSpan.FromSeconds(1), async () =>
        {
            await SweepAsync();
            (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Delivered);
            PayoutsOf(order).ShouldBeEmpty();
        });

        // At two days: released, and the payout is recorded and sent in the same sweep.
        await WithClockAsync(deliveredAt + TimeSpan.FromDays(2), async () =>
        {
            var summary = await SweepAsync();
            summary.ReleasedOrders.ShouldBeGreaterThanOrEqualTo(1);

            (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Released);
            var payout = PayoutsOf(order).ShouldHaveSingleItem();
            payout.Amount.ShouldBe(Money.FromRupiah(950_000)); // Rp1.000.000 of items less the 5% fee; shipping is not the seller's
            payout.Fee.ShouldBe(Money.FromRupiah(50_000));
            payout.Status.ShouldBe(PayoutStatus.Paid);
            SentFor(order).ShouldHaveSingleItem().Destination!.Value.AccountNumber.ShouldBe("1234567890");
        });
    }

    [Fact]
    public async Task A_buyer_who_confirms_receipt_releases_the_money_at_once_even_before_the_courier_reports_delivery()
    {
        var buyer = NewId();
        var seller = NewId();
        await SeedBankAccountAsync(seller);
        var order = ShippedOrder(buyer, seller);
        using var client = factory.CreateClientAs(buyer, "confirmer");

        using var response = await client.PostAsync($"/api/orders/{order.Id}/release", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Released);
        PayoutsOf(order).ShouldHaveSingleItem().Status.ShouldBe(PayoutStatus.Paid);
    }

    [Fact]
    public async Task Confirming_twice_owes_the_seller_once()
    {
        var buyer = NewId();
        var seller = NewId();
        await SeedBankAccountAsync(seller);
        var order = ShippedOrder(buyer, seller);
        using var client = factory.CreateClientAs(buyer, "double.confirmer");

        (await client.PostAsync($"/api/orders/{order.Id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"/api/orders/{order.Id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        PayoutsOf(order).Count.ShouldBe(1);
        SentFor(order).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_bank_account_the_payout_waits_and_goes_out_once_one_is_added()
    {
        var buyer = NewId();
        var seller = NewId();
        var order = ShippedOrder(buyer, seller);
        using var client = factory.CreateClientAs(buyer, "no.bank");
        (await client.PostAsync($"/api/orders/{order.Id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var waiting = PayoutsOf(order).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(PayoutStatus.Pending);
        waiting.LastError.ShouldBe(PayoutSender.MissingBankAccount);
        SentFor(order).ShouldBeEmpty();

        await SeedBankAccountAsync(seller);
        await WithClockAsync(factory.Clock.UtcNow + TimeSpan.FromHours(2), async () =>
        {
            await SweepAsync();
            PayoutsOf(order).ShouldHaveSingleItem().Status.ShouldBe(PayoutStatus.Paid);
            SentFor(order).ShouldHaveSingleItem();
        });
    }

    [Fact]
    public async Task Saving_a_bank_account_sends_the_waiting_payout_at_once()
    {
        var buyer = NewId();
        var seller = NewId();
        var order = ShippedOrder(buyer, seller);
        using var buyerClient = factory.CreateClientAs(buyer, "bank.later.buyer");
        (await buyerClient.PostAsync($"/api/orders/{order.Id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        PayoutsOf(order).ShouldHaveSingleItem().Status.ShouldBe(PayoutStatus.Pending);

        using var sellerClient = factory.CreateClientAs(seller, "bank.later.seller");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/profile/bank-account")
        {
            Content = JsonContent.Create(new { bankName = "BCA", accountNumber = "1234567890", accountHolderName = "Penjual Uji" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await sellerClient.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // No sweep and no waiting: the money goes out in the same request that fixed what it waited for.
        PayoutsOf(order).ShouldHaveSingleItem().Status.ShouldBe(PayoutStatus.Paid);
        SentFor(order).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_payment_provider_failure_never_undoes_the_release_and_the_payout_is_retried()
    {
        var buyer = NewId();
        var seller = NewId();
        await SeedBankAccountAsync(seller);
        var order = ShippedOrder(buyer, seller);
        factory.PayoutProvider.NextResult = PayoutResult.Refused("provider down", retryable: true);
        using var client = factory.CreateClientAs(buyer, "provider.down");

        (await client.PostAsync($"/api/orders/{order.Id}/release", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Released);
        var pending = PayoutsOf(order).Single();
        pending.Status.ShouldBe(PayoutStatus.Pending);
        pending.Attempts.ShouldBe(1);

        factory.PayoutProvider.NextResult = PayoutResult.Sent("ref-2");
        await WithClockAsync(factory.Clock.UtcNow + TimeSpan.FromMinutes(6), async () =>
        {
            await SweepAsync();
            PayoutsOf(order).Single().Status.ShouldBe(PayoutStatus.Paid);
        });
        factory.PayoutProvider.NextResult = PayoutResult.Sent("fake-ref");
    }

    [Fact]
    public async Task A_disputed_parcel_is_never_released_by_the_timer()
    {
        var buyer = NewId();
        var seller = NewId();
        var order = ShippedOrder(buyer, seller);
        order.MarkDelivered(factory.Clock);
        order.OpenDispute("Tidak sesuai deskripsi", buyer, factory.Clock);
        factory.Orders.Seed(order);

        await WithClockAsync(factory.Clock.UtcNow + TimeSpan.FromDays(10), async () =>
        {
            await SweepAsync();
            (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Disputed);
        });
    }

    [Fact]
    public async Task With_no_courier_provider_a_parcel_waits_for_the_buyer_instead_of_being_guessed_delivered()
    {
        var order = ShippedOrder(NewId(), NewId());
        factory.Courier.IsConfigured = false;
        try
        {
            await WithClockAsync(factory.Clock.UtcNow + TimeSpan.FromDays(20), async () =>
            {
                await SweepAsync();
                (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Shipped);
            });
        }
        finally
        {
            factory.Courier.IsConfigured = true;
        }
    }

    [Fact]
    public async Task A_parcel_still_on_the_way_is_asked_about_again_later_not_on_every_sweep()
    {
        var order = ShippedOrder(NewId(), NewId());
        factory.Courier.Report = new DeliveryReport(DeliveryState.InTransit);
        factory.Courier.Asked.Clear();
        var shippedAt = factory.Clock.UtcNow;

        await WithClockAsync(shippedAt + TimeSpan.FromMinutes(10), async () =>
        {
            await SweepAsync();
            factory.Courier.Asked.ShouldNotContain(order.Id);
        });

        await WithClockAsync(shippedAt + Order.TrackingPollInterval, async () =>
        {
            await SweepAsync();
            factory.Courier.Asked.Count(id => id == order.Id).ShouldBe(1);
            await SweepAsync();
            factory.Courier.Asked.Count(id => id == order.Id).ShouldBe(1);
            (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.InTransit);
        });
    }
}
