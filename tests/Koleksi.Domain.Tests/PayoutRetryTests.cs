using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class PayoutRetryTests
{
    private static (Payout Payout, TestClock Clock) WaitingPayout()
    {
        var clock = TestClock.AtEpoch();
        var order = Order.Create(
            "buyer",
            "seller",
            [new OrderLine("L1", "Card", "listings/L1/0.jpg", Money.FromRupiah(500_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), EstimatedDays: 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "proof/a.jpg", "proof/b.jpg", new DateOnly(2026, 1, 1)), "seller", clock);
        order.Release(confirmedByBuyer: true, clock);

        var payout = Payout.For(order, clock);
        payout.BeginAttempt(clock);
        payout.RecordFailure("bank_account_missing", retryable: false, clock);
        return (payout, clock);
    }

    [Fact]
    public void A_refusal_that_needs_the_seller_waits_at_least_an_hour()
    {
        var (payout, clock) = WaitingPayout();

        payout.NextAttemptAt.ShouldBeGreaterThanOrEqualTo(clock.UtcNow + TimeSpan.FromHours(1));
    }

    [Fact]
    public void Retry_now_makes_a_waiting_payout_due_immediately()
    {
        var (payout, clock) = WaitingPayout();
        var version = payout.Version;

        payout.RetryNow(clock);

        payout.NextAttemptAt.ShouldBe(clock.UtcNow);
        payout.Version.ShouldBe(version + 1);
        payout.Status.ShouldBe(PayoutStatus.Pending);
    }

    [Fact]
    public void Retry_now_does_nothing_to_a_payout_that_is_already_paid()
    {
        var (payout, clock) = WaitingPayout();
        payout.MarkPaid("ref-1", clock);
        var version = payout.Version;

        payout.RetryNow(clock);

        payout.Version.ShouldBe(version);
        payout.Status.ShouldBe(PayoutStatus.Paid);
    }
}
