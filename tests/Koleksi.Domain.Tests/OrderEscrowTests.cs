using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class OrderEscrowTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly CourierSelection Jne =
        new("jne", "JNE REG", Money.FromRupiah(24_000), EstimatedDays: 3);

    private static (Order Order, TestClock Clock) PaidOrder()
    {
        var (order, clock) = NewOrder();
        order.ConfirmPayment(order.GrandTotal, clock);
        return (order, clock);
    }

    private static (Order Order, TestClock Clock) NewOrder()
    {
        var clock = TestClock.AtEpoch();
        var order = Order.Create(
            Buyer,
            Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", "listings/L1/0.jpg", Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            Jne,
            clock);
        return (order, clock);
    }

    private static Shipment ValidShipment() => Shipment.Create(
        "jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 1, 1));

    [Fact]
    public void Totals_add_shipping_to_items_and_leave_buyer_protection_free()
    {
        var (order, _) = NewOrder();

        order.ItemsTotal.ShouldBe(Money.FromRupiah(4_250_000));
        order.ShippingTotal.ShouldBe(Money.FromRupiah(24_000));
        order.GrandTotal.ShouldBe(Money.FromRupiah(4_274_000));
    }

    [Fact]
    public void The_platform_fee_comes_out_of_the_sellers_payout_not_the_buyers_total()
    {
        var (order, _) = NewOrder();

        order.PlatformFee.ShouldBe(Money.FromRupiah(212_500));
        order.SellerPayout.ShouldBe(Money.FromRupiah(4_037_500));
        (order.SellerPayout + order.PlatformFee).ShouldBe(order.ItemsTotal);
    }

    [Fact]
    public void A_buyer_cannot_purchase_their_own_listing() =>
        Should.Throw<DomainException>(() => Order.Create(
            Seller,
            Seller,
            [new OrderLine("L1", "x", null, Money.FromRupiah(100_000), 1)],
            new ShippingAddress("a", "b", "c", "d", "e", "f"),
            Jne,
            TestClock.AtEpoch())).Code.ShouldBe("order.self_purchase");

    [Fact]
    public void A_settlement_that_does_not_match_the_total_never_opens_escrow()
    {
        var (order, clock) = NewOrder();

        // A tampered or truncated callback claiming a smaller settlement.
        Should.Throw<DomainException>(() => order.ConfirmPayment(Money.FromRupiah(1_000), clock))
            .Code.ShouldBe("order.amount_mismatch");

        order.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public void A_repeated_payment_webhook_is_a_no_op()
    {
        var (order, clock) = PaidOrder();
        var versionAfterFirst = order.Version;

        // Gateways retry. The second delivery must not advance or corrupt state.
        Should.NotThrow(() => order.ConfirmPayment(order.GrandTotal, clock));

        order.EscrowState.ShouldBe(EscrowState.Paid);
        order.Version.ShouldBe(versionAfterFirst);
    }

    [Fact]
    public void The_full_happy_path_walks_the_five_timeline_steps()
    {
        var (order, clock) = PaidOrder();
        order.EscrowState.ShouldBe(EscrowState.Paid);

        order.MarkShipped(ValidShipment(), Seller, clock);
        order.EscrowState.ShouldBe(EscrowState.Shipped);

        order.MarkInTransit(clock);
        order.EscrowState.ShouldBe(EscrowState.InTransit);

        order.MarkDelivered(clock);
        order.EscrowState.ShouldBe(EscrowState.Delivered);

        order.Release(confirmedByBuyer: true, clock);
        order.EscrowState.ShouldBe(EscrowState.Released);
        order.ReleasedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Funds_cannot_be_released_before_the_parcel_is_shipped()
    {
        var (order, clock) = PaidOrder();

        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: true, clock))
            .Code.ShouldBe("order.not_releasable");
    }

    [Fact]
    public void The_timed_release_never_starts_before_a_courier_reported_delivery()
    {
        var (order, clock) = PaidOrder();
        order.MarkShipped(ValidShipment(), Seller, clock);
        clock.Advance(TimeSpan.FromDays(30));

        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: false, clock))
            .Code.ShouldBe("order.not_releasable");
        order.EscrowState.ShouldBe(EscrowState.Shipped);
    }

    [Fact]
    public void Auto_release_waits_for_the_buyers_window_to_close()
    {
        var (order, clock) = PaidOrder();
        order.MarkShipped(ValidShipment(), Seller, clock);
        order.MarkDelivered(clock);

        // The sweep runs early: the buyer still has time to inspect and dispute.
        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: false, clock))
            .Code.ShouldBe("order.release_window_open");

        clock.Advance(Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1));
        Should.NotThrow(() => order.Release(confirmedByBuyer: false, clock));
        order.EscrowState.ShouldBe(EscrowState.Released);
    }

    [Fact]
    public void An_open_dispute_freezes_release()
    {
        var (order, clock) = PaidOrder();
        order.MarkShipped(ValidShipment(), Seller, clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Item does not match the description", Buyer, clock);

        order.EscrowState.ShouldBe(EscrowState.Disputed);
        Should.Throw<DomainException>(() => order.Release(confirmedByBuyer: true, clock))
            .Code.ShouldBe("order.disputed");
    }

    [Fact]
    public void Resolving_a_dispute_for_the_buyer_refunds()
    {
        var (order, clock) = PaidOrder();
        order.MarkShipped(ValidShipment(), Seller, clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Counterfeit", Buyer, clock);

        order.ResolveDispute(DisputeResolution.RefundToBuyer, clock);

        order.EscrowState.ShouldBe(EscrowState.Refunded);
        order.Dispute!.ResolvedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Only_the_seller_can_mark_an_order_shipped()
    {
        var (order, clock) = PaidOrder();

        Should.Throw<DomainException>(() => order.MarkShipped(ValidShipment(), Buyer, clock))
            .Code.ShouldBe("order.forbidden");
    }

    [Fact]
    public void The_seller_cannot_substitute_a_different_courier()
    {
        var (order, clock) = PaidOrder();
        var wrongCourier = Shipment.Create(
            "sicepat", "SC0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 1, 1));

        Should.Throw<DomainException>(() => order.MarkShipped(wrongCourier, Seller, clock))
            .Code.ShouldBe("order.courier_mismatch");
    }

    [Fact]
    public void A_short_tracking_number_is_rejected() =>
        Should.Throw<DomainException>(() => Shipment.Create(
            "jne", "JNE12", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 1, 1)))
            .Code.ShouldBe("shipment.tracking_too_short");

    [Fact]
    public void An_unrelated_user_is_not_a_participant()
    {
        var (order, _) = PaidOrder();

        Should.Throw<DomainException>(() => order.EnsureParticipant("stranger-sub"))
            .Code.ShouldBe("order.forbidden");
        Should.NotThrow(() => order.EnsureParticipant(Buyer));
        Should.NotThrow(() => order.EnsureParticipant(Seller));
    }

    [Fact]
    public void An_unpaid_order_expires_after_the_payment_window()
    {
        var (order, clock) = NewOrder();

        order.HasPaymentExpired(clock).ShouldBeFalse();
        clock.Advance(Order.PaymentWindow + TimeSpan.FromMinutes(1));
        order.HasPaymentExpired(clock).ShouldBeTrue();
    }

    [Fact]
    public void The_order_number_does_not_leak_sequence_information()
    {
        var (first, _) = NewOrder();
        var (second, _) = NewOrder();

        first.OrderNumber.ShouldStartWith("KLK-");
        first.OrderNumber.ShouldNotBe(second.OrderNumber);
    }
}
