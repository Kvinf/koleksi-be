using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>
/// The owner's escrow and payment-deadline rules: two days after delivery, thirty minutes to pay for an
/// auction, fifteen for anything else, and a buyer who can always confirm their own parcel.
/// </summary>
public sealed class OrderReleaseRulesTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), EstimatedDays: 3);

    private static Order NewOrder(TestClock clock, OrderSource source = OrderSource.FixedPrice, DateTimeOffset? due = null) =>
        Order.Create(
            Buyer,
            Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            Jne,
            clock,
            source,
            due);

    private static Order Shipped(TestClock clock)
    {
        var order = NewOrder(clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 1, 1)),
            Seller,
            clock);
        return order;
    }

    [Fact]
    public void The_confirmation_window_after_delivery_is_two_days() =>
        Order.AutoReleaseAfterDelivery.ShouldBe(TimeSpan.FromDays(2));

    [Fact]
    public void Funds_release_on_their_own_exactly_two_days_after_the_courier_reports_delivery()
    {
        var clock = TestClock.AtEpoch();
        var order = Shipped(clock);
        clock.Advance(TimeSpan.FromDays(5));
        order.MarkDelivered(clock);
        var deliveredAt = clock.UtcNow;

        order.AutoReleaseAt.ShouldBe(deliveredAt + TimeSpan.FromDays(2));

        clock.Advance(TimeSpan.FromDays(2) - TimeSpan.FromSeconds(1));
        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: false, clock))
            .Code.ShouldBe("order.release_window_open");

        clock.Advance(TimeSpan.FromSeconds(1));
        order.Release(confirmedByBuyer: false, clock);
        order.EscrowState.ShouldBe(EscrowState.Released);
    }

    [Fact]
    public void A_buyer_who_has_the_parcel_can_confirm_it_even_before_the_courier_says_so()
    {
        var clock = TestClock.AtEpoch();
        var order = Shipped(clock);

        order.Release(confirmedByBuyer: true, clock);

        order.EscrowState.ShouldBe(EscrowState.Released);
        order.DeliveredAt.ShouldNotBeNull();
        order.ReleasedAt.ShouldNotBeNull();
    }

    [Fact]
    public void A_buyer_cannot_confirm_a_parcel_that_was_never_shipped()
    {
        var clock = TestClock.AtEpoch();
        var order = NewOrder(clock);
        order.ConfirmPayment(order.GrandTotal, clock);

        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: true, clock))
            .Code.ShouldBe("order.not_releasable");
    }

    [Fact]
    public void An_open_dispute_stops_both_confirmation_and_the_timed_release()
    {
        var clock = TestClock.AtEpoch();
        var order = Shipped(clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Not as described", Buyer, clock);
        clock.Advance(TimeSpan.FromDays(10));

        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: false, clock)).Code.ShouldBe("order.disputed");
        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: true, clock)).Code.ShouldBe("order.disputed");
    }

    [Fact]
    public void An_unpaid_sale_is_due_in_fifteen_minutes()
    {
        var clock = TestClock.AtEpoch();
        var order = NewOrder(clock);

        order.PaymentDueAt.ShouldBe(clock.UtcNow + TimeSpan.FromMinutes(15));
        order.HasPaymentExpired(clock).ShouldBeFalse();

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        order.HasPaymentExpired(clock).ShouldBeTrue();
    }

    [Fact]
    public void An_auction_win_is_due_thirty_minutes_after_the_close_whatever_time_checkout_opens()
    {
        var clock = TestClock.AtEpoch();
        var closedAt = clock.UtcNow;
        clock.Advance(TimeSpan.FromMinutes(20));

        var order = NewOrder(clock, OrderSource.AuctionWin, closedAt + Order.AuctionPaymentWindow);

        order.PaymentDueAt.ShouldBe(closedAt + TimeSpan.FromMinutes(30));
        order.Source.ShouldBe(OrderSource.AuctionWin);
        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        order.HasPaymentExpired(clock).ShouldBeTrue();
    }

    [Fact]
    public void An_order_cannot_be_opened_once_its_deadline_has_already_passed()
    {
        var clock = TestClock.AtEpoch();

        Should.Throw<DomainException>(() => NewOrder(clock, OrderSource.AuctionWin, clock.UtcNow - TimeSpan.FromSeconds(1)))
            .Code.ShouldBe("order.payment_window_closed");
    }

    [Fact]
    public void The_auction_winner_window_and_the_order_window_are_the_same_thirty_minutes() =>
        Koleksi.Domain.Auctions.Auction.WinnerPaymentWindow.ShouldBe(Order.AuctionPaymentWindow);
}
