using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>How an offer is stored, and that rows written before counters were kept apart still read.</summary>
public sealed class OfferItemTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

    private static Offer Pending() => Offer.Create(
        "L1", Buyer, Seller, Money.FromRupiah(1_500_000), Money.FromRupiah(2_000_000), "Bisa?", null, Clock,
        "rangga.wh", "budi.collectibles");

    [Fact]
    public void A_countered_offer_round_trips_with_both_amounts_and_both_handles()
    {
        var offer = Pending();
        offer.Counter(Money.FromRupiah(1_800_000), Seller, Clock);

        var read = OfferItem.ToDomain(OfferItem.ToItem(offer));

        read.Amount.Rupiah.ShouldBe(1_500_000);
        read.CounterAmount!.Value.Rupiah.ShouldBe(1_800_000);
        read.CurrentAmount.Rupiah.ShouldBe(1_800_000);
        read.Status.ShouldBe(OfferStatus.Countered);
        read.BuyerUsername.ShouldBe("rangga.wh");
        read.SellerUsername.ShouldBe("budi.collectibles");
        read.Version.ShouldBe(offer.Version);
        read.ExpiresAt.ShouldBe(offer.ExpiresAt);
    }

    [Fact]
    public void A_checked_out_offer_round_trips_its_order()
    {
        var offer = Pending();
        offer.Accept(Seller, Clock);
        offer.MarkCheckedOut(Buyer, "order-1", Clock);

        var read = OfferItem.ToDomain(OfferItem.ToItem(offer));

        read.Status.ShouldBe(OfferStatus.CheckedOut);
        read.OrderId.ShouldBe("order-1");
    }

    [Fact]
    public void A_row_without_the_newer_attributes_reads_as_an_offer_with_no_counter_and_no_names()
    {
        // Exactly what was stored before counters, handles and orders existed.
        var item = OfferItem.ToItem(Pending());
        foreach (var attribute in new[] { "counterAmount", "buyerUsername", "sellerUsername", "orderId" })
        {
            item.Remove(attribute).ShouldBeTrue();
        }

        var read = OfferItem.ToDomain(item);

        read.CounterAmount.ShouldBeNull();
        read.BuyerUsername.ShouldBeNull();
        read.SellerUsername.ShouldBeNull();
        read.OrderId.ShouldBeNull();
        read.Amount.Rupiah.ShouldBe(1_500_000);
        read.Status.ShouldBe(OfferStatus.Pending);
    }

    [Fact]
    public void A_legacy_countered_row_reads_its_stored_amount_as_the_counter()
    {
        // Old counters overwrote "amount", so a Countered row with no counterAmount holds the counter there.
        var item = OfferItem.ToItem(Pending());
        item["status"] = new AttributeValue { S = "Countered" };
        item.Remove("counterAmount");

        var read = OfferItem.ToDomain(item);

        read.Status.ShouldBe(OfferStatus.Countered);
        read.CounterAmount!.Value.Rupiah.ShouldBe(1_500_000);
        read.CurrentAmount.Rupiah.ShouldBe(1_500_000);
    }

    [Theory]
    [InlineData(OfferStatus.Pending, true)]
    [InlineData(OfferStatus.Countered, true)]
    [InlineData(OfferStatus.Accepted, true)]
    [InlineData(OfferStatus.Declined, false)]
    [InlineData(OfferStatus.Expired, false)]
    [InlineData(OfferStatus.Withdrawn, false)]
    [InlineData(OfferStatus.CheckedOut, true)] // its order may die unpaid and reopen the deal (D-08)
    public void Only_a_live_deal_keeps_the_buyers_active_offer_pointer(OfferStatus status, bool active) =>
        OfferItem.IsActiveStatus(status).ShouldBe(active);

    [Fact]
    public void An_absent_counter_is_stored_as_null_not_zero()
    {
        var item = OfferItem.ToItem(Pending());

        item["counterAmount"].NULL.ShouldBe(true);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
