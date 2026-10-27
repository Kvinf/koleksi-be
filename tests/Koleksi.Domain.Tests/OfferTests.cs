using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class OfferTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";
    private const string Stranger = "stranger-sub";
    private static readonly Money Asking = Money.FromRupiah(2_000_000);

    private static Offer Create(long amount, decimal? autoDecline, TestClock clock) => Offer.Create(
        "L1", Buyer, Seller, Money.FromRupiah(amount), Asking, message: null, autoDecline, clock);

    private static Offer Countered(TestClock clock, long offered = 1_500_000, long counter = 1_800_000)
    {
        var offer = Create(offered, null, clock);
        offer.Counter(Money.FromRupiah(counter), Seller, clock);
        return offer;
    }

    private static Offer Accepted(TestClock clock)
    {
        var offer = Create(1_700_000, null, clock);
        offer.Accept(Seller, clock);
        return offer;
    }

    private static string CodeOf(Action act) => Should.Throw<DomainException>(act).Code;

    // ─── creation ───────────────────────────────────────────────────────────

    [Fact]
    public void A_valid_offer_opens_pending_with_a_24_hour_window()
    {
        var clock = TestClock.AtEpoch();

        var offer = Create(1_700_000, autoDecline: null, clock);

        offer.Status.ShouldBe(OfferStatus.Pending);
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.Validity);
        offer.PercentOfAsking.ShouldBe(85m);
        offer.CounterAmount.ShouldBeNull();
        offer.CurrentAmount.ShouldBe(Money.FromRupiah(1_700_000));
    }

    [Fact]
    public void An_offer_at_or_above_asking_is_rejected() =>
        CodeOf(() => Create(2_000_000, null, TestClock.AtEpoch())).ShouldBe("offer.not_below_asking");

    [Fact]
    public void An_offer_below_the_platform_floor_is_rejected() =>
        CodeOf(() => Create(100_000, null, TestClock.AtEpoch())).ShouldBe("offer.too_low");

    [Fact]
    public void A_seller_threshold_declines_immediately_rather_than_leaving_the_buyer_waiting()
    {
        var offer = Create(1_000_000, autoDecline: 70m, TestClock.AtEpoch());

        offer.Status.ShouldBe(OfferStatus.Declined);
        offer.RespondedAt.ShouldNotBeNull();
    }

    [Fact]
    public void An_offer_above_the_sellers_threshold_stays_pending() =>
        Create(1_600_000, autoDecline: 70m, TestClock.AtEpoch()).Status.ShouldBe(OfferStatus.Pending);

    [Fact]
    public void A_buyer_cannot_offer_on_their_own_listing() =>
        CodeOf(() => Offer.Create(
            "L1", Seller, Seller, Money.FromRupiah(1_000_000), Asking, null, null, TestClock.AtEpoch()))
            .ShouldBe("offer.self_offer");

    [Fact]
    public void Both_handles_are_kept_when_given()
    {
        var offer = Offer.Create(
            "L1", Buyer, Seller, Money.FromRupiah(1_700_000), Asking, null, null, TestClock.AtEpoch(),
            buyerUsername: "rangga.wh", sellerUsername: "budi.collectibles");

        offer.BuyerUsername.ShouldBe("rangga.wh");
        offer.SellerUsername.ShouldBe("budi.collectibles");
    }

    // ─── Pending: the seller's move ─────────────────────────────────────────

    [Fact]
    public void The_seller_accepts_a_pending_offer_at_the_buyers_amount_and_the_buyer_gets_48_hours_to_pay()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);
        clock.Advance(TimeSpan.FromHours(3));

        offer.Accept(Seller, clock);

        offer.Status.ShouldBe(OfferStatus.Accepted);
        offer.CurrentAmount.ShouldBe(Money.FromRupiah(1_700_000));
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.CheckoutWindow);
    }

    [Fact]
    public void The_seller_declines_a_pending_offer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);

        offer.Decline(Seller, clock);

        offer.Status.ShouldBe(OfferStatus.Declined);
        offer.RespondedAt.ShouldBe(clock.UtcNow);
    }

    [Fact]
    public void The_buyer_cannot_accept_or_decline_their_own_pending_offer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);

        CodeOf(() => offer.Accept(Buyer, clock)).ShouldBe("offer.not_your_turn.forbidden");
        CodeOf(() => offer.Decline(Buyer, clock)).ShouldBe("offer.not_your_turn.forbidden");
        offer.Status.ShouldBe(OfferStatus.Pending);
    }

    [Fact]
    public void A_stranger_cannot_act_on_any_state()
    {
        var clock = TestClock.AtEpoch();
        var pending = Create(1_700_000, null, clock);
        var countered = Countered(clock);

        CodeOf(() => pending.Accept(Stranger, clock)).ShouldBe("offer.forbidden");
        CodeOf(() => pending.Decline(Stranger, clock)).ShouldBe("offer.forbidden");
        CodeOf(() => pending.Counter(Money.FromRupiah(1_900_000), Stranger, clock)).ShouldBe("offer.forbidden");
        CodeOf(() => pending.Withdraw(Stranger, clock)).ShouldBe("offer.forbidden");
        CodeOf(() => countered.Accept(Stranger, clock)).ShouldBe("offer.forbidden");
    }

    // ─── counter ────────────────────────────────────────────────────────────

    [Fact]
    public void A_counter_keeps_the_buyers_amount_and_restarts_the_window_for_the_buyer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);
        clock.Advance(TimeSpan.FromHours(20));

        offer.Counter(Money.FromRupiah(1_800_000), Seller, clock);

        offer.Status.ShouldBe(OfferStatus.Countered);
        offer.Amount.ShouldBe(Money.FromRupiah(1_500_000));
        offer.CounterAmount.ShouldBe(Money.FromRupiah(1_800_000));
        offer.CurrentAmount.ShouldBe(Money.FromRupiah(1_800_000));
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.Validity);
    }

    [Fact]
    public void A_counter_must_be_above_the_buyers_offer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);

        CodeOf(() => offer.Counter(Money.FromRupiah(1_400_000), Seller, clock)).ShouldBe("offer.counter_not_higher");
        CodeOf(() => offer.Counter(Money.FromRupiah(1_500_000), Seller, clock)).ShouldBe("offer.counter_not_higher");
    }

    [Fact]
    public void A_counter_cannot_exceed_the_asking_price_but_may_equal_it()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);

        CodeOf(() => offer.Counter(Money.FromRupiah(2_500_000), Seller, clock)).ShouldBe("offer.counter_above_asking");

        offer.Counter(Asking, Seller, clock);
        offer.CounterAmount.ShouldBe(Asking);
    }

    [Fact]
    public void Only_one_counter_is_allowed_because_the_second_is_not_the_sellers_turn()
    {
        var clock = TestClock.AtEpoch();
        var offer = Countered(clock);

        CodeOf(() => offer.Counter(Money.FromRupiah(1_900_000), Seller, clock)).ShouldBe("offer.not_your_turn.forbidden");
        offer.CounterAmount.ShouldBe(Money.FromRupiah(1_800_000));
    }

    [Fact]
    public void The_buyer_cannot_counter()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);

        CodeOf(() => offer.Counter(Money.FromRupiah(1_900_000), Buyer, clock)).ShouldBe("offer.not_your_turn.forbidden");
    }

    // ─── Countered: the buyer's move ────────────────────────────────────────

    [Fact]
    public void The_buyer_accepts_a_counter_and_pays_the_counter_amount()
    {
        var clock = TestClock.AtEpoch();
        var offer = Countered(clock);

        offer.Accept(Buyer, clock);

        offer.Status.ShouldBe(OfferStatus.Accepted);
        offer.CurrentAmount.ShouldBe(Money.FromRupiah(1_800_000));
        offer.Amount.ShouldBe(Money.FromRupiah(1_500_000));
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.CheckoutWindow);
    }

    [Fact]
    public void The_buyer_declines_a_counter()
    {
        var clock = TestClock.AtEpoch();
        var offer = Countered(clock);

        offer.Decline(Buyer, clock);

        offer.Status.ShouldBe(OfferStatus.Declined);
    }

    [Fact]
    public void The_seller_cannot_accept_or_decline_a_row_they_already_countered()
    {
        var clock = TestClock.AtEpoch();
        var offer = Countered(clock);

        // Accepting would commit the buyer to a price without their consent.
        CodeOf(() => offer.Accept(Seller, clock)).ShouldBe("offer.not_your_turn.forbidden");
        CodeOf(() => offer.Decline(Seller, clock)).ShouldBe("offer.not_your_turn.forbidden");
        offer.Status.ShouldBe(OfferStatus.Countered);
    }

    // ─── Withdraw ───────────────────────────────────────────────────────────

    [Fact]
    public void The_buyer_can_withdraw_a_pending_or_countered_offer()
    {
        var clock = TestClock.AtEpoch();
        var pending = Create(1_700_000, null, clock);
        var countered = Countered(clock);

        pending.Withdraw(Buyer, clock);
        countered.Withdraw(Buyer, clock);

        pending.Status.ShouldBe(OfferStatus.Withdrawn);
        countered.Status.ShouldBe(OfferStatus.Withdrawn);
    }

    [Fact]
    public void The_seller_cannot_withdraw_the_buyers_offer()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => Create(1_700_000, null, clock).Withdraw(Seller, clock)).ShouldBe("offer.forbidden");
    }

    [Fact]
    public void An_accepted_or_settled_offer_cannot_be_withdrawn()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => Accepted(clock).Withdraw(Buyer, clock)).ShouldBe("offer.not_withdrawable");
    }

    // ─── Settled offers ─────────────────────────────────────────────────────

    [Fact]
    public void An_already_resolved_offer_cannot_be_resolved_twice()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);

        CodeOf(() => offer.Decline(Seller, clock)).ShouldBe("offer.already_resolved");
        CodeOf(() => offer.Accept(Seller, clock)).ShouldBe("offer.already_resolved");
        CodeOf(() => offer.Accept(Buyer, clock)).ShouldBe("offer.already_resolved");
    }

    // ─── Expiry ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_expired_offer_cannot_be_accepted()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);

        clock.Advance(Offer.Validity + TimeSpan.FromMinutes(1));

        CodeOf(() => offer.Accept(Seller, clock)).ShouldBe("offer.expired");
    }

    [Fact]
    public void A_lapsed_counter_cannot_be_accepted_by_the_buyer_or_withdrawn()
    {
        var clock = TestClock.AtEpoch();
        var offer = Countered(clock);

        clock.Advance(Offer.Validity + TimeSpan.FromMinutes(1));

        CodeOf(() => offer.Accept(Buyer, clock)).ShouldBe("offer.expired");
        CodeOf(() => offer.Withdraw(Buyer, clock)).ShouldBe("offer.expired");
    }

    [Fact]
    public void Expiry_is_idempotent_so_the_sweep_can_run_repeatedly()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);
        clock.Advance(Offer.Validity + TimeSpan.FromMinutes(1));

        offer.Expire(clock).ShouldBeTrue();
        var version = offer.Version;
        offer.Expire(clock).ShouldBeFalse();

        offer.Status.ShouldBe(OfferStatus.Expired);
        offer.Version.ShouldBe(version);
    }

    [Fact]
    public void Expire_leaves_a_live_or_settled_offer_alone()
    {
        var clock = TestClock.AtEpoch();
        var live = Create(1_700_000, null, clock);
        var declined = Create(1_700_000, null, clock);
        declined.Decline(Seller, clock);
        clock.Advance(TimeSpan.FromDays(30));

        live.Expire(TestClock.AtEpoch()).ShouldBeFalse();
        declined.Expire(clock).ShouldBeFalse();
        declined.Status.ShouldBe(OfferStatus.Declined);
    }

    [Fact]
    public void An_accepted_offer_that_is_never_paid_for_expires_after_48_hours()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);

        clock.Advance(Offer.CheckoutWindow - TimeSpan.FromMinutes(1));
        offer.EffectiveStatus(clock).ShouldBe(OfferStatus.Accepted);

        clock.Advance(TimeSpan.FromMinutes(2));
        offer.EffectiveStatus(clock).ShouldBe(OfferStatus.Expired);
        offer.Status.ShouldBe(OfferStatus.Accepted);

        offer.Expire(clock).ShouldBeTrue();
        offer.Status.ShouldBe(OfferStatus.Expired);
    }

    [Fact]
    public void Terminal_states_never_read_as_expired()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);
        offer.Decline(Seller, clock);
        clock.Advance(TimeSpan.FromDays(30));

        offer.EffectiveStatus(clock).ShouldBe(OfferStatus.Declined);
    }

    // ─── Checkout: an accepted offer pays for one order ─────────────────────

    [Fact]
    public void An_accepted_offer_is_spent_by_checkout_and_cannot_be_spent_twice()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);

        offer.MarkCheckedOut(Buyer, "order-1", clock);

        offer.Status.ShouldBe(OfferStatus.CheckedOut);
        offer.OrderId.ShouldBe("order-1");
        CodeOf(() => offer.MarkCheckedOut(Buyer, "order-2", clock)).ShouldBe("offer.already_used");
        offer.OrderId.ShouldBe("order-1");
    }

    [Fact]
    public void Only_the_buyer_can_check_out_an_offer()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => Accepted(clock).MarkCheckedOut(Seller, "o", clock)).ShouldBe("offer.forbidden");
        CodeOf(() => Accepted(clock).MarkCheckedOut(Stranger, "o", clock)).ShouldBe("offer.forbidden");
    }

    [Fact]
    public void An_offer_that_is_not_accepted_cannot_be_checked_out()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => Create(1_700_000, null, clock).MarkCheckedOut(Buyer, "o", clock)).ShouldBe("offer.not_accepted");
        CodeOf(() => Countered(clock).MarkCheckedOut(Buyer, "o", clock)).ShouldBe("offer.not_accepted");
    }

    [Fact]
    public void A_lapsed_accepted_offer_cannot_be_checked_out()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);
        clock.Advance(Offer.CheckoutWindow + TimeSpan.FromMinutes(1));

        CodeOf(() => offer.MarkCheckedOut(Buyer, "o", clock)).ShouldBe("offer.expired");
    }

    [Fact]
    public void An_unpaid_dead_order_gives_the_deal_back_within_the_window()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);
        offer.MarkCheckedOut(Buyer, "order-1", clock);
        clock.Advance(TimeSpan.FromHours(1));

        offer.ReopenAfterUnpaidOrder(clock);

        offer.Status.ShouldBe(OfferStatus.Accepted);
        offer.OrderId.ShouldBeNull();
        offer.ExpiresAt.ShouldBe(TestClock.AtEpoch().UtcNow + Offer.CheckoutWindow);
    }

    [Fact]
    public void Reopening_after_the_window_closed_expires_the_offer_instead()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);
        offer.MarkCheckedOut(Buyer, "order-1", clock);
        clock.Advance(Offer.CheckoutWindow + TimeSpan.FromHours(1));

        offer.ReopenAfterUnpaidOrder(clock);

        offer.Status.ShouldBe(OfferStatus.Expired);
    }

    [Fact]
    public void Reopening_an_offer_that_was_never_checked_out_changes_nothing()
    {
        var clock = TestClock.AtEpoch();
        var offer = Accepted(clock);
        var version = offer.Version;

        offer.ReopenAfterUnpaidOrder(clock);

        offer.Status.ShouldBe(OfferStatus.Accepted);
        offer.Version.ShouldBe(version);
    }

    [Fact]
    public void Every_transition_advances_the_version_by_one()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);
        offer.Version.ShouldBe(0);

        offer.Counter(Money.FromRupiah(1_800_000), Seller, clock);
        offer.Version.ShouldBe(1);
        offer.Accept(Buyer, clock);
        offer.Version.ShouldBe(2);
        offer.MarkCheckedOut(Buyer, "o", clock);
        offer.Version.ShouldBe(3);
    }
}
