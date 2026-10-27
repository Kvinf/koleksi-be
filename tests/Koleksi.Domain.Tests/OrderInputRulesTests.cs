using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>Bounds on what a buyer or seller can write onto an order: dispute text and waybill.</summary>
public sealed class OrderInputRulesTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static Order PaidOrder(TestClock clock)
    {
        var order = Order.Create(
            Buyer,
            Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        return order;
    }

    [Fact]
    public void A_second_dispute_is_refused_and_keeps_the_first_reason_and_time()
    {
        var clock = TestClock.AtEpoch();
        var order = PaidOrder(clock);
        order.OpenDispute("Alasan pertama.", Buyer, clock);
        var openedAt = order.Dispute!.OpenedAt;
        var version = order.Version;

        clock.Advance(TimeSpan.FromHours(2));
        var failure = Should.Throw<DomainException>(() => order.OpenDispute("Alasan kedua.", Buyer, clock));

        failure.Code.ShouldBe("order.already_disputed");
        order.Dispute.Reason.ShouldBe("Alasan pertama.");
        order.Dispute.OpenedAt.ShouldBe(openedAt);
        order.Version.ShouldBe(version);
    }

    [Fact]
    public void A_dispute_can_be_opened_again_only_after_nothing_remains_open_which_it_cannot_once_resolved()
    {
        // Once support rules, the order leaves Disputed (released or refunded), and the existing
        // already-released / not-disputable rules apply rather than a second dispute.
        var clock = TestClock.AtEpoch();
        var order = PaidOrder(clock);
        order.OpenDispute("Barang rusak.", Buyer, clock);
        order.ResolveDispute(DisputeResolution.RefundToBuyer, clock);

        Should.Throw<DomainException>(() => order.OpenDispute("Lagi.", Buyer, clock))
            .Code.ShouldBe("order.not_disputable");
    }

    [Fact]
    public void The_reason_is_trimmed_and_must_not_be_blank()
    {
        var clock = TestClock.AtEpoch();
        var order = PaidOrder(clock);

        Should.Throw<DomainException>(() => order.OpenDispute("   ", Buyer, clock))
            .Code.ShouldBe("order.dispute_reason_required");

        order.OpenDispute("  rusak  ", Buyer, clock);
        order.Dispute!.Reason.ShouldBe("rusak");
    }

    [Fact]
    public void A_reason_over_the_limit_is_refused_and_one_at_the_limit_is_kept()
    {
        var clock = TestClock.AtEpoch();

        Should.Throw<DomainException>(
                () => PaidOrder(clock).OpenDispute(new string('x', Dispute.MaxReasonLength + 1), Buyer, clock))
            .Code.ShouldBe("order.dispute_reason_too_long");

        var order = PaidOrder(clock);
        order.OpenDispute(new string('x', Dispute.MaxReasonLength), Buyer, clock);
        order.Dispute!.Reason.Length.ShouldBe(Dispute.MaxReasonLength);
    }

    [Fact]
    public void The_seller_cannot_open_a_dispute()
    {
        var clock = TestClock.AtEpoch();

        Should.Throw<DomainException>(() => PaidOrder(clock).OpenDispute("Saya penjual.", Seller, clock))
            .Code.ShouldBe("order.forbidden");
    }

    [Fact]
    public void A_tracking_number_is_normalised_and_bounded_in_length()
    {
        var shippedOn = new DateOnly(2026, 1, 1);

        Shipment.Create("jne", "  jx1234567890  ", "p/packed.jpg", "p/label.jpg", shippedOn)
            .TrackingNumber.ShouldBe("JX1234567890");

        Should.Throw<DomainException>(() => Shipment.Create("jne", "", "p/a", "p/b", shippedOn))
            .Code.ShouldBe("shipment.tracking_too_short");

        Should.Throw<DomainException>(() => Shipment.Create(
                "jne", new string('9', Shipment.MaxTrackingNumberLength + 1), "p/a", "p/b", shippedOn))
            .Code.ShouldBe("shipment.tracking_too_long");

        Shipment.Create("jne", new string('9', Shipment.MaxTrackingNumberLength), "p/a", "p/b", shippedOn)
            .TrackingNumber.Length.ShouldBe(Shipment.MaxTrackingNumberLength);
    }

    [Fact]
    public void A_paid_order_cannot_be_disputed_for_not_shipping_until_the_ship_window_has_passed()
    {
        var clock = TestClock.AtEpoch();
        var order = PaidOrder(clock);

        order.DisputableFrom.ShouldBe(clock.UtcNow + Order.ShipWindow);

        Should.Throw<DomainException>(() => order.EnsureDisputeWindowOpen(clock))
            .Code.ShouldBe("order.dispute_too_early");

        clock.Advance(Order.ShipWindow - TimeSpan.FromSeconds(1));
        Should.Throw<DomainException>(() => order.EnsureDisputeWindowOpen(clock));

        clock.Advance(TimeSpan.FromSeconds(1));
        Should.NotThrow(() => order.EnsureDisputeWindowOpen(clock));
    }

    [Fact]
    public void Once_shipped_the_window_no_longer_applies()
    {
        var clock = TestClock.AtEpoch();
        var order = PaidOrder(clock);
        order.MarkShipped(
            Shipment.Create("jne", "JX1234567890", "p/packed.jpg", "p/label.jpg", new DateOnly(2026, 1, 1)), Seller, clock);

        Should.NotThrow(() => order.EnsureDisputeWindowOpen(clock));
    }

    [Fact]
    public void One_photo_cannot_be_both_the_parcel_proof_and_the_label_proof()
    {
        var shippedOn = new DateOnly(2026, 1, 1);

        Should.Throw<DomainException>(() => Shipment.Create("jne", "JX1234567890", "p/same.jpg", "p/same.jpg", shippedOn))
            .Code.ShouldBe("shipment.proof_duplicate");

        // Keys are compared exactly: the store's keys are case-sensitive.
        Shipment.Create("jne", "JX1234567890", "p/same.jpg", "p/SAME.jpg", shippedOn)
            .PackedParcelPhotoKey.ShouldBe("p/same.jpg");
    }
}
