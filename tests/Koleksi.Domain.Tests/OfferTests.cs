using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class OfferTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";
    private static readonly Money Asking = Money.FromRupiah(2_000_000);

    private static Offer Create(long amount, decimal? autoDecline, TestClock clock) => Offer.Create(
        "L1", Buyer, Seller, Money.FromRupiah(amount), Asking, message: null, autoDecline, clock);

    [Fact]
    public void A_valid_offer_opens_pending_with_a_24_hour_window()
    {
        var clock = TestClock.AtEpoch();

        var offer = Create(1_700_000, autoDecline: null, clock);

        offer.Status.ShouldBe(OfferStatus.Pending);
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.Validity);
        offer.PercentOfAsking.ShouldBe(85m);
    }

    [Fact]
    public void An_offer_at_or_above_asking_is_rejected() =>
        Should.Throw<DomainException>(() => Create(2_000_000, null, TestClock.AtEpoch()))
            .Code.ShouldBe("offer.not_below_asking");

    [Fact]
    public void An_offer_below_the_platform_floor_is_rejected() =>
        Should.Throw<DomainException>(() => Create(100_000, null, TestClock.AtEpoch()))
            .Code.ShouldBe("offer.too_low");

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
        Should.Throw<DomainException>(() => Offer.Create(
            "L1", Seller, Seller, Money.FromRupiah(1_000_000), Asking, null, null, TestClock.AtEpoch()))
            .Code.ShouldBe("offer.self_offer");

    [Fact]
    public void An_expired_offer_cannot_be_accepted()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);

        clock.Advance(Offer.Validity + TimeSpan.FromMinutes(1));

        Should.Throw<DomainException>(() => offer.Accept(Seller, clock)).Code.ShouldBe("offer.expired");
    }

    [Fact]
    public void Only_the_seller_can_respond()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);

        Should.Throw<DomainException>(() => offer.Accept(Buyer, clock)).Code.ShouldBe("offer.forbidden");
    }

    [Fact]
    public void A_countered_offer_restarts_the_window_for_the_buyer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);
        clock.Advance(TimeSpan.FromHours(20));

        offer.Counter(Money.FromRupiah(1_800_000), Seller, clock);

        offer.Status.ShouldBe(OfferStatus.Countered);
        offer.Amount.ShouldBe(Money.FromRupiah(1_800_000));
        offer.ExpiresAt.ShouldBe(clock.UtcNow + Offer.Validity);
    }

    [Fact]
    public void A_counter_must_be_above_the_buyers_offer()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);

        Should.Throw<DomainException>(() => offer.Counter(Money.FromRupiah(1_400_000), Seller, clock))
            .Code.ShouldBe("offer.counter_not_higher");
    }

    [Fact]
    public void A_counter_cannot_exceed_the_asking_price()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_500_000, null, clock);

        Should.Throw<DomainException>(() => offer.Counter(Money.FromRupiah(2_500_000), Seller, clock))
            .Code.ShouldBe("offer.counter_above_asking");
    }

    [Fact]
    public void An_already_resolved_offer_cannot_be_resolved_twice()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);
        offer.Accept(Seller, clock);

        Should.Throw<DomainException>(() => offer.Decline(Seller, clock)).Code.ShouldBe("offer.already_resolved");
    }

    [Fact]
    public void Expiry_is_idempotent_so_the_sweep_can_run_repeatedly()
    {
        var clock = TestClock.AtEpoch();
        var offer = Create(1_700_000, null, clock);
        clock.Advance(Offer.Validity + TimeSpan.FromMinutes(1));

        offer.Expire(clock);
        var version = offer.Version;
        offer.Expire(clock);

        offer.Status.ShouldBe(OfferStatus.Expired);
        offer.Version.ShouldBe(version);
    }
}
