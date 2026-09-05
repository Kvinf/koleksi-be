using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Checkout, fulfilment, and the payment gateway's own webhook, end to end.</summary>
public sealed class OrderEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task Checking_out_a_fixed_price_listing_creates_an_order_and_starts_payment()
    {
        var listing = SeedFixedPriceListing();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("paymentUnavailable").GetBoolean().ShouldBeFalse();
        body.GetProperty("paymentRedirectUrl").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("grandTotalRupiah").GetInt64().ShouldBe(listing.Price.Rupiah + 15_000);
    }

    [Fact]
    public async Task Checkout_still_creates_the_order_when_the_payment_gateway_is_unavailable()
    {
        var listing = SeedFixedPriceListing();
        factory.Payments.CreateSessionFailsWith = DomainException.For(
            "payment.not_configured", "Payment gateway credentials are not configured for this environment.");
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        try
        {
            using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.ReadJsonAsync();
            body.GetProperty("paymentUnavailable").GetBoolean().ShouldBeTrue();
            body.GetProperty("orderId").GetString().ShouldNotBeNullOrEmpty();
        }
        finally
        {
            factory.Payments.CreateSessionFailsWith = null;
        }
    }

    [Fact]
    public async Task Checkout_for_an_accepted_offer_uses_the_agreed_price_not_the_asking_price()
    {
        var listing = SeedNegoListing();
        var offer = Offer.Create(
            listing.Id, TestData.BidderId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa kurang?", autoDeclineBelowPercent: null, new FixedTestClock());
        offer.Accept(listing.SellerId, new FixedTestClock());
        factory.Offers.Seed(offer);

        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AcceptedOffer", offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("grandTotalRupiah").GetInt64().ShouldBe(2_000_000 + 15_000);
    }

    [Fact]
    public async Task A_stranger_to_the_order_gets_the_same_not_found_a_bad_id_would()
    {
        var order = SeedPaidOrder();
        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await client.GetAsync($"/api/orders/{order.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_found");
    }

    [Fact]
    public async Task The_buyer_and_seller_each_see_their_own_role_on_the_order()
    {
        var order = SeedPaidOrder();

        using var buyerClient = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var buyerResponse = await buyerClient.GetAsync($"/api/orders/{order.Id}");
        buyerResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await buyerResponse.ReadJsonAsync()).GetProperty("viewerRole").GetString().ShouldBe("buyer");

        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var sellerResponse = await sellerClient.GetAsync($"/api/orders/{order.Id}");
        sellerResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sellerResponse.ReadJsonAsync()).GetProperty("viewerRole").GetString().ShouldBe("seller");
    }

    [Fact]
    public async Task An_unpaid_order_cannot_be_marked_shipped()
    {
        var order = SeedAwaitingPaymentOrder();
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            new { trackingNumber = "JX1234567890", packedParcelPhotoKey = "k1", trackingLabelPhotoKey = "k2", shippedOn = "2026-01-05" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_shippable");
    }

    [Fact]
    public async Task The_seller_can_mark_a_paid_order_shipped_and_the_buyer_can_then_confirm_delivery()
    {
        var order = SeedPaidOrder();
        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var shipResponse = await sellerClient.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            new { trackingNumber = "JX1234567890", packedParcelPhotoKey = "k1", trackingLabelPhotoKey = "k2", shippedOn = "2026-01-05" });

        shipResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await shipResponse.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Shipped");

        // Courier-confirmed delivery has no client-facing endpoint yet — it is meant to
        // come from a tracking webhook that does not exist in this environment (the same
        // documented gap as the auto-close sweep). Applied directly here to reach the
        // state /release actually requires.
        var shippedOrder = await factory.Orders.FindAsync(order.Id, CancellationToken.None);
        shippedOrder.ShouldNotBeNull();
        shippedOrder.MarkDelivered(new FixedTestClock());
        await factory.Orders.UpdateAsync(shippedOrder, shippedOrder.Version - 1, CancellationToken.None);

        using var buyerClient = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var releaseResponse = await buyerClient.PostAsync($"/api/orders/{order.Id}/release", null);

        releaseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await releaseResponse.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Released");
    }

    [Fact]
    public async Task The_seller_cannot_confirm_their_own_delivery()
    {
        var order = SeedPaidOrder();
        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await sellerClient.PostAsync($"/api/orders/{order.Id}/release", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.forbidden");
    }

    [Fact]
    public async Task A_buyer_can_open_a_dispute_on_a_paid_order()
    {
        var order = SeedPaidOrder();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{order.Id}/dispute", new { reason = "Barang tidak sesuai deskripsi." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Disputed");
    }

    [Fact]
    public async Task An_unverified_webhook_notification_is_rejected()
    {
        factory.Payments.NextNotification = null;
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/payments/webhook", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_settled_webhook_notification_pays_the_order_and_marks_the_listing_sold()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        factory.Orders.Seed(order);

        factory.Payments.NextNotification = new PaymentNotification(
            order.OrderNumber, PaymentStatus.Settled, order.GrandTotal, "PROC-1", $"evt-{order.Id}");

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/payments/webhook", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updatedOrder = await factory.Orders.FindAsync(order.Id, CancellationToken.None);
        updatedOrder.ShouldNotBeNull();
        updatedOrder.EscrowState.ShouldBe(EscrowState.Paid);

        var updatedListing = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        updatedListing.ShouldNotBeNull();
        updatedListing.Status.ShouldBe(ListingStatus.Sold);
    }

    private static object CheckoutBody(string source, string referenceId) => new
    {
        source,
        referenceId,
        shipTo = new
        {
            recipientName = "Rangga Wijaya",
            phone = "081234567890",
            street = "Jl. Merdeka No. 1",
            city = "Bandung",
            province = "Jawa Barat",
            postalCode = "40111",
        },
        courierCode = "jne",
    };

    private static ShippingAddress SampleShippingAddress() =>
        new("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111");

    private static CourierSelection FlatRateCourier() => Koleksi.Application.Features.Orders.FlatRateCouriers.Find("jne")!;

    private Listing SeedFixedPriceListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        return listing;
    }

    private Listing SeedNegoListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        return listing;
    }

    private Order SeedAwaitingPaymentOrder()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        factory.Orders.Seed(order);
        return order;
    }

    private Order SeedPaidOrder()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        order.ConfirmPayment(order.GrandTotal, new FixedTestClock());
        factory.Orders.Seed(order);
        return order;
    }

    private sealed class FixedTestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = MutableClock.Origin;
    }
}
