using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Reviews;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>A buyer reviewing an order, and the seller's public feed of what they wrote.</summary>
public sealed class ReviewEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    // A buyer and a seller of their own per test: the review bucket is per user and shared by the
    // whole fixture, and a seller's feed would otherwise mix every test's reviews together.
    private readonly string _buyerId = Guid.NewGuid().ToString();
    private readonly string _sellerId = Guid.NewGuid().ToString();
    private const string BuyerSignInName = "pembeli.rahasia@mail.test";
    private const string SellerSignInName = "penjual.rahasia@mail.test";

    [Fact]
    public async Task The_buyer_of_a_delivered_order_can_review_it_once()
    {
        var order = SeedOrder(EscrowState.Delivered);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);

        using var first = await buyer.PostAsJsonAsync(
            $"/api/orders/{order.Id}/review", new { rating = 5, text = "Barang sesuai deskripsi", anonymous = false });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.ReadJsonAsync()).GetProperty("reviewId").GetString().ShouldNotBeNullOrEmpty();

        using var second = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/review", new { rating = 1 });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("review.already_reviewed");
    }

    [Fact]
    public async Task A_replay_with_the_same_idempotency_key_returns_the_same_review_and_does_not_conflict()
    {
        var order = SeedOrder(EscrowState.Released);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);
        var key = Guid.NewGuid().ToString();

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/review")
        {
            Content = JsonContent.Create(new { rating = 4 }),
        };
        firstRequest.Headers.Add("Idempotency-Key", key);
        using var first = await buyer.SendAsync(firstRequest);

        using var secondRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/review")
        {
            Content = JsonContent.Create(new { rating = 4 }),
        };
        secondRequest.Headers.Add("Idempotency-Key", key);
        using var second = await buyer.SendAsync(secondRequest);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.ReadJsonAsync()).GetProperty("reviewId").GetString()
            .ShouldBe((await first.ReadJsonAsync()).GetProperty("reviewId").GetString());
    }

    [Theory]
    [InlineData(EscrowState.Paid)]
    [InlineData(EscrowState.Shipped)]
    public async Task An_order_that_has_not_been_delivered_cannot_be_reviewed(EscrowState state)
    {
        var order = SeedOrder(state);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);

        using var response = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/review", new { rating = 5 });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("review.not_eligible");
    }

    [Fact]
    public async Task The_seller_and_a_stranger_get_the_same_not_found_as_a_missing_order()
    {
        var order = SeedOrder(EscrowState.Delivered);
        using var seller = factory.CreateClientAs(_sellerId, SellerSignInName);
        using var stranger = factory.CreateClientAs(Guid.NewGuid().ToString(), "stranger");

        foreach (var client in new[] { seller, stranger })
        {
            using var write = await client.PostAsJsonAsync($"/api/orders/{order.Id}/review", new { rating = 5 });
            write.StatusCode.ShouldBe(HttpStatusCode.NotFound);

            using var read = await client.GetAsync($"/api/orders/{order.Id}/review");
            read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var missing = await seller.PostAsJsonAsync(
            $"/api/orders/{TestData.UnknownListingId}/review", new { rating = 5 });
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task A_rating_outside_one_to_five_is_a_bad_request(int rating)
    {
        var order = SeedOrder(EscrowState.Delivered);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);

        using var response = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/review", new { rating });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("review.invalid_rating");
    }

    [Fact]
    public async Task A_review_over_the_length_limit_is_refused_and_not_stored()
    {
        var order = SeedOrder(EscrowState.Delivered);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);

        using var response = await buyer.PostAsJsonAsync(
            $"/api/orders/{order.Id}/review", new { rating = 4, text = new string('x', Review.MaxTextLength + 1) });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("review.text_too_long");
        (await factory.Reviews.FindByOrderAsync(order.Id, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task The_order_page_can_ask_whether_to_offer_a_review_and_then_see_it_was_written()
    {
        var delivered = SeedOrder(EscrowState.Delivered);
        var shipped = SeedOrder(EscrowState.Shipped);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);

        var before = await (await buyer.GetAsync($"/api/orders/{delivered.Id}/review")).ReadJsonAsync();
        before.GetProperty("eligible").GetBoolean().ShouldBeTrue();
        before.TryGetProperty("review", out _).ShouldBeFalse();

        var notYet = await (await buyer.GetAsync($"/api/orders/{shipped.Id}/review")).ReadJsonAsync();
        notYet.GetProperty("eligible").GetBoolean().ShouldBeFalse();

        using var write = await buyer.PostAsJsonAsync(
            $"/api/orders/{delivered.Id}/review", new { rating = 3, text = "Cukup", anonymous = true });
        write.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await (await buyer.GetAsync($"/api/orders/{delivered.Id}/review")).ReadJsonAsync();
        after.GetProperty("eligible").GetBoolean().ShouldBeFalse();
        after.GetProperty("review").GetProperty("rating").GetInt32().ShouldBe(3);
        after.GetProperty("review").GetProperty("anonymous").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task The_public_feed_lists_reviews_newest_first_and_never_reveals_an_anonymous_buyer()
    {
        var named = SeedOrder(EscrowState.Delivered);
        using var buyer = factory.CreateClientAs(_buyerId, BuyerSignInName);
        using var namedWrite = await buyer.PostAsJsonAsync(
            $"/api/orders/{named.Id}/review", new { rating = 5, text = "Mantap", anonymous = false });
        namedWrite.StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));

        var otherBuyerId = Guid.NewGuid().ToString();
        var anonymousOrder = SeedOrder(EscrowState.Released, otherBuyerId);
        using var otherBuyer = factory.CreateClientAs(otherBuyerId, "diam.diam@mail.test");
        using var anonymousWrite = await otherBuyer.PostAsJsonAsync(
            $"/api/orders/{anonymousOrder.Id}/review", new { rating = 2, text = "Kurang", anonymous = true });
        anonymousWrite.StatusCode.ShouldBe(HttpStatusCode.OK);

        var handle = factory.PublicHandleOf(_sellerId);
        using var anonymousVisitor = factory.CreateClient();
        using var response = await anonymousVisitor.GetAsync($"/api/sellers/{handle}/reviews");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.ReadTextAsync();
        text.ShouldNotContain(_buyerId);
        text.ShouldNotContain(otherBuyerId);
        text.ShouldNotContain("@");

        var items = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(2);
        items[0].GetProperty("rating").GetInt32().ShouldBe(2);
        items[0].GetProperty("isAnonymous").GetBoolean().ShouldBeTrue();
        items[0].TryGetProperty("reviewerName", out _).ShouldBeFalse();
        items[1].GetProperty("reviewerName").GetString().ShouldBe(factory.PublicHandleOf(_buyerId));
        items[1].GetProperty("text").GetString().ShouldBe("Mantap");
    }

    [Fact]
    public async Task Reviews_page_with_a_cursor_and_the_storefront_counts_them()
    {
        for (var i = 0; i < 3; i++)
        {
            var buyerId = Guid.NewGuid().ToString();
            var order = SeedOrder(EscrowState.Delivered, buyerId);
            using var client = factory.CreateClientAs(buyerId, "b" + i);
            using var write = await client.PostAsJsonAsync($"/api/orders/{order.Id}/review", new { rating = 3 + (i % 3) });
            write.StatusCode.ShouldBe(HttpStatusCode.OK);
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var handle = factory.PublicHandleOf(_sellerId);
        using var visitor = factory.CreateClient();

        var firstPage = await (await visitor.GetAsync($"/api/sellers/{handle}/reviews?limit=2")).ReadJsonAsync();
        firstPage.GetProperty("items").GetArrayLength().ShouldBe(2);
        var cursor = firstPage.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var secondPage = await (await visitor.GetAsync($"/api/sellers/{handle}/reviews?limit=2&cursor={Uri.EscapeDataString(cursor!)}")).ReadJsonAsync();
        secondPage.GetProperty("items").GetArrayLength().ShouldBe(1);

        var shop = await (await visitor.GetAsync($"/api/sellers/{handle}")).ReadJsonAsync();
        var rating = shop.GetProperty("rating");
        rating.GetProperty("count").GetInt32().ShouldBe(3);
        rating.GetProperty("average").GetDecimal().ShouldBe(4m);
        rating.GetProperty("histogram").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([0, 0, 1, 1, 1]);
    }

    private Order SeedOrder(EscrowState target, string? buyerId = null)
    {
        var clock = new MutableClock();
        var order = Order.Create(
            buyerId ?? _buyerId,
            _sellerId,
            [new OrderLine(TestData.UnknownListingId, "PSA 10 Charizard", null, Money.FromRupiah(2_500_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
            clock);

        if (target != EscrowState.AwaitingPayment)
        {
            order.ConfirmPayment(order.GrandTotal, clock);
        }

        if (target is EscrowState.Shipped or EscrowState.Delivered or EscrowState.Released)
        {
            order.MarkShipped(
                Shipment.Create("jne", "JNE0012345678", "p/packed.jpg", "p/label.jpg", new DateOnly(2026, 3, 1)),
                _sellerId,
                clock);
        }

        if (target is EscrowState.Delivered or EscrowState.Released)
        {
            order.MarkDelivered(clock);
        }

        if (target == EscrowState.Released)
        {
            order.Release(confirmedByBuyer: true, clock);
        }

        factory.Orders.Seed(order);
        return order;
    }
}
