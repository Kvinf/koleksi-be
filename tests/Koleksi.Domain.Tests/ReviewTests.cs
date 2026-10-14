using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Reviews;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class ReviewTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static Order OrderIn(EscrowState target, TestClock clock)
    {
        var order = Order.Create(
            Buyer,
            Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);

        if (target == EscrowState.AwaitingPayment)
        {
            return order;
        }

        order.ConfirmPayment(order.GrandTotal, clock);
        if (target == EscrowState.Paid)
        {
            return order;
        }

        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "p/packed.jpg", "p/label.jpg", new DateOnly(2026, 1, 1)),
            Seller,
            clock);
        if (target == EscrowState.Shipped)
        {
            return order;
        }

        order.MarkDelivered(clock);
        if (target == EscrowState.Delivered)
        {
            return order;
        }

        order.Release(confirmedByBuyer: true, clock);
        return order;
    }

    [Theory]
    [InlineData(EscrowState.Delivered)]
    [InlineData(EscrowState.Released)]
    public void The_buyer_can_review_once_the_item_was_delivered(EscrowState state)
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(state, clock);

        var review = Review.Create(order, Buyer, 5, "Barang sesuai deskripsi", isAnonymous: false, clock);

        review.OrderId.ShouldBe(order.Id);
        review.SellerId.ShouldBe(Seller);
        review.BuyerId.ShouldBe(Buyer);
        review.ListingId.ShouldBe("L1");
        review.Rating.ShouldBe(5);
        review.Text.ShouldBe("Barang sesuai deskripsi");
        review.IsAnonymous.ShouldBeFalse();
        Review.CanReview(order, Buyer).ShouldBeTrue();
    }

    [Theory]
    [InlineData(EscrowState.AwaitingPayment)]
    [InlineData(EscrowState.Paid)]
    [InlineData(EscrowState.Shipped)]
    public void An_order_the_buyer_has_not_received_cannot_be_reviewed(EscrowState state)
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(state, clock);

        Should.Throw<DomainException>(() => Review.Create(order, Buyer, 5, null, false, clock))
            .Code.ShouldBe("review.not_eligible");
        Review.CanReview(order, Buyer).ShouldBeFalse();
    }

    [Fact]
    public void A_disputed_order_cannot_be_reviewed_until_it_resolves()
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(EscrowState.Delivered, clock);
        order.OpenDispute("Barang tidak sesuai", Buyer, clock);

        Should.Throw<DomainException>(() => Review.Create(order, Buyer, 1, null, false, clock))
            .Code.ShouldBe("review.not_eligible");
    }

    [Fact]
    public void Only_the_buyer_can_review_and_a_seller_cannot_review_their_own_sale()
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(EscrowState.Delivered, clock);

        Should.Throw<DomainException>(() => Review.Create(order, Seller, 5, null, false, clock))
            .Code.ShouldBe("review.self_review");
        Should.Throw<DomainException>(() => Review.Create(order, "stranger-sub", 5, null, false, clock))
            .Code.ShouldBe("review.forbidden");
        Review.CanReview(order, Seller).ShouldBeFalse();
        Review.CanReview(order, "stranger-sub").ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void A_rating_outside_one_to_five_is_refused(int rating)
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(EscrowState.Delivered, clock);

        Should.Throw<DomainException>(() => Review.Create(order, Buyer, rating, null, false, clock))
            .Code.ShouldBe("review.invalid_rating");
    }

    [Fact]
    public void The_text_is_cleaned_like_a_chat_message_and_capped()
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(EscrowState.Delivered, clock);

        var cleaned = Review.Create(order, Buyer, 4, "  Mantap\u0007!\r\nCepat  ", false, clock);
        cleaned.Text.ShouldBe("Mantap!\nCepat");

        var blank = Review.Create(order, Buyer, 4, "   ", false, clock);
        blank.Text.ShouldBeNull();

        Should.Throw<DomainException>(() =>
                Review.Create(order, Buyer, 4, new string('x', Review.MaxTextLength + 1), false, clock))
            .Code.ShouldBe("review.text_too_long");
        Should.NotThrow(() => Review.Create(order, Buyer, 4, new string('x', Review.MaxTextLength), false, clock));
    }

    [Fact]
    public void An_anonymous_flag_is_kept_on_the_review()
    {
        var clock = TestClock.AtEpoch();
        var order = OrderIn(EscrowState.Released, clock);

        Review.Create(order, Buyer, 3, null, isAnonymous: true, clock).IsAnonymous.ShouldBeTrue();
    }

    [Fact]
    public void Stats_average_to_one_decimal_and_say_nothing_when_there_are_no_reviews()
    {
        SellerRatingStats.Empty.Average.ShouldBeNull();

        new SellerRatingStats(3, 13, [0, 0, 1, 1, 1]).Average.ShouldBe(4.3m);
        new SellerRatingStats(2, 9, [0, 0, 0, 1, 1]).Average.ShouldBe(4.5m);
    }
}
