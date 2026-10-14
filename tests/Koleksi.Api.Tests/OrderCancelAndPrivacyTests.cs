using System.Net;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// What a seller may see of a buyer's address (QA S-02), and the buyer cancelling an order they
/// have not paid for.
/// </summary>
public sealed class OrderCancelAndPrivacyTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string BuyerName = "Rangga Wijaya";
    private const string BuyerPhone = "081234567890";
    private const string BuyerStreet = "Jl. Merdeka No. 1";

    private sealed class FixedTestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = MutableClock.Origin;
    }

    private Order Seed(Action<Order>? advance = null)
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            new ShippingAddress(BuyerName, BuyerPhone, BuyerStreet, "Bandung", "Jawa Barat", "40111"),
            Koleksi.Application.Features.Orders.FlatRateCouriers.Find("jne")!, new FixedTestClock());
        advance?.Invoke(order);
        factory.Orders.Seed(order);
        return order;
    }

    private async Task<string> WireAsync(string userId, string username, string orderId)
    {
        using var client = factory.CreateClientAs(userId, username);
        using var response = await client.GetAsync($"/api/orders/{orderId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadTextAsync();
    }

    private async Task<System.Text.Json.JsonElement> DetailAsync(string userId, string username, string orderId) =>
        System.Text.Json.JsonDocument.Parse(await WireAsync(userId, username, orderId)).RootElement.GetProperty("order");

    // --- S-02: the address stays with the buyer until the order is paid -------------------------

    [Fact]
    public async Task The_seller_sees_no_address_on_an_unpaid_order_but_the_buyer_still_sees_their_own()
    {
        var order = Seed();

        var forSeller = await DetailAsync(TestData.SellerId, TestData.SellerUsername, order.Id);
        forSeller.TryGetProperty("shipTo", out _).ShouldBeFalse();

        var forBuyer = await DetailAsync(TestData.BidderId, TestData.BidderUsername, order.Id);
        forBuyer.GetProperty("shipTo").GetProperty("recipientName").GetString().ShouldBe(BuyerName);
        forBuyer.GetProperty("shipTo").GetProperty("street").GetString().ShouldBe(BuyerStreet);
    }

    [Fact]
    public async Task The_seller_sees_no_address_on_a_cancelled_order()
    {
        var order = Seed(o => o.Cancel(new FixedTestClock()));

        var wire = await WireAsync(TestData.SellerId, TestData.SellerUsername, order.Id);

        wire.ShouldNotContain(BuyerPhone);
        wire.ShouldNotContain(BuyerStreet);
        wire.ShouldNotContain(BuyerName);

        var forBuyer = await DetailAsync(TestData.BidderId, TestData.BidderUsername, order.Id);
        forBuyer.GetProperty("shipTo").GetProperty("phone").GetString().ShouldBe(BuyerPhone);
    }

    [Fact]
    public async Task The_seller_sees_the_address_once_the_order_is_paid()
    {
        var order = Seed(o => o.ConfirmPayment(o.GrandTotal, new FixedTestClock()));

        var forSeller = await DetailAsync(TestData.SellerId, TestData.SellerUsername, order.Id);

        var shipTo = forSeller.GetProperty("shipTo");
        shipTo.GetProperty("recipientName").GetString().ShouldBe(BuyerName);
        shipTo.GetProperty("phone").GetString().ShouldBe(BuyerPhone);
        shipTo.GetProperty("street").GetString().ShouldBe(BuyerStreet);
    }

    // --- Cancelling an unpaid order -------------------------------------------------------------

    [Fact]
    public async Task The_buyer_cancels_an_unpaid_order_and_the_item_is_free_for_someone_else()
    {
        var order = Seed();
        var listingId = order.Lines[0].ListingId;
        (await factory.Holds.TryHoldAsync(listingId, TestData.BidderId, order.Id, CancellationToken.None)).Acquired.ShouldBeTrue();
        (await factory.Holds.TryHoldAsync(listingId, TestData.RivalId, "other-order", CancellationToken.None))
            .Acquired.ShouldBeFalse("the buyer's hold blocks everyone else until it is released");

        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var response = await buyer.PostAsync($"/api/orders/{order.Id}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("escrowState").GetString().ShouldBe("Cancelled");
        body.GetProperty("viewerRole").GetString().ShouldBe("buyer");
        body.GetProperty("order").GetProperty("id").GetString().ShouldBe(order.Id);
        body.GetProperty("order").GetProperty("escrowState").GetString().ShouldBe("Cancelled");

        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Cancelled);
        (await factory.Holds.TryHoldAsync(listingId, TestData.RivalId, "other-order", CancellationToken.None))
            .Acquired.ShouldBeTrue("cancelling releases the listing hold");
    }

    [Fact]
    public async Task A_cancel_retried_with_the_same_key_answers_the_same_and_without_one_is_a_conflict()
    {
        var order = Seed();
        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        var key = Guid.NewGuid().ToString();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/cancel");
            request.Headers.Add("Idempotency-Key", key);
            using var response = await buyer.SendAsync(request);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt}");
            (await response.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Cancelled");
        }

        using var again = await buyer.PostAsync($"/api/orders/{order.Id}/cancel", null);
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_cancellable");
    }

    [Fact]
    public async Task A_paid_order_cannot_be_cancelled_and_is_left_alone()
    {
        var order = Seed(o => o.ConfirmPayment(o.GrandTotal, new FixedTestClock()));
        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await buyer.PostAsync($"/api/orders/{order.Id}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_cancellable");
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task A_stranger_gets_the_same_not_found_for_a_real_order_as_for_a_missing_one()
    {
        var order = Seed();
        using var stranger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var real = await stranger.PostAsync($"/api/orders/{order.Id}/cancel", null);
        using var missing = await stranger.PostAsync($"/api/orders/{TestData.UnknownListingId}/cancel", null);

        real.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await real.ReadTextAsync()).ShouldBe(await missing.ReadTextAsync());
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public async Task The_seller_is_a_party_but_cannot_cancel_the_buyers_order()
    {
        var order = Seed();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await seller.PostAsync($"/api/orders/{order.Id}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.forbidden");
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public async Task Cancelling_requires_a_signed_in_caller()
    {
        var order = Seed();
        using var visitor = factory.CreateClient();

        using var response = await visitor.PostAsync($"/api/orders/{order.Id}/cancel", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
