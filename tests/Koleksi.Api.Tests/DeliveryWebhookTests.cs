using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Sweeps;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// A third-party courier-tracking provider pushes "in transit" and "delivered" to
/// <c>POST /api/shipping/webhook/{provider}</c>. Delivery starts the two-day release clock, so an unverified body
/// must change nothing, and a repeated, late or reordered notice must never move an order twice or backwards.
/// </summary>
public sealed class DeliveryWebhookTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), EstimatedDays: 3);

    private static readonly ShippingAddress Address =
        new("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810");

    private static string NewId() => Guid.NewGuid().ToString();

    private Order SeedOrder(bool shipped = true)
    {
        var buyer = NewId();
        var seller = NewId();
        var order = Order.Create(
            buyer, seller, [new OrderLine(NewId(), "Kartu uji", null, Money.FromRupiah(1_000_000), 1)],
            Address, Jne, factory.Clock);
        order.ConfirmPayment(order.GrandTotal, factory.Clock);
        if (shipped)
        {
            order.MarkShipped(
                Shipment.Create("jne", "JNE0012345678", "proof/packed.jpg", "proof/label.jpg", new DateOnly(2026, 3, 1)),
                seller,
                factory.Clock);
        }

        factory.Orders.Seed(order);
        return order;
    }

    private async Task<HttpResponseMessage> PostAsync(string provider, string body, string? signature = "ok")
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/shipping/webhook/{provider}")
        {
            Content = new StringContent(body),
        };
        if (signature is not null)
        {
            request.Headers.Add("X-Test-Signature", signature);
        }

        return await client.SendAsync(request);
    }

    private async Task<Order> Reload(Order order) =>
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!;

    [Fact]
    public async Task A_verified_delivered_notice_marks_the_order_delivered_and_starts_the_two_day_clock()
    {
        var order = SeedOrder();

        using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await Reload(order);
        after.EscrowState.ShouldBe(EscrowState.Delivered);
        after.AutoReleaseAt.ShouldBe(factory.Clock.UtcNow + Order.AutoReleaseAfterDelivery);
    }

    [Fact]
    public async Task An_in_transit_notice_moves_a_shipped_order_but_not_to_delivered()
    {
        var order = SeedOrder();

        using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|in_transit");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.InTransit);
    }

    [Fact]
    public async Task A_notice_with_a_bad_signature_changes_nothing_and_is_refused()
    {
        var order = SeedOrder();

        using var wrong = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered", signature: "forged");
        using var missing = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered", signature: null);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Shipped);
    }

    [Fact]
    public async Task An_unknown_provider_is_refused_exactly_like_a_bad_signature()
    {
        var order = SeedOrder();

        using var unknown = await PostAsync("nobody", $"evt-{NewId()}|{order.OrderNumber}|delivered");
        using var forged = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered", signature: "forged");

        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await unknown.Content.ReadAsStringAsync()).ShouldBe(await forged.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_applied_once()
    {
        var order = SeedOrder();
        var body = $"evt-{NewId()}|{order.OrderNumber}|delivered";

        using var first = await PostAsync("fake", body);
        var deliveredAt = (await Reload(order)).DeliveredAt;
        factory.Clock.UtcNow += TimeSpan.FromHours(5);
        try
        {
            using var second = await PostAsync("fake", body);

            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The clock did not restart: a replay must not push the release date out.
            (await Reload(order)).DeliveredAt.ShouldBe(deliveredAt);
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task A_late_in_transit_notice_never_moves_a_delivered_order_backwards()
    {
        var order = SeedOrder();
        using var delivered = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered");

        using var late = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|in_transit");

        late.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Delivered);
    }

    [Fact]
    public async Task A_notice_other_than_in_transit_or_delivered_is_acknowledged_and_ignored()
    {
        var order = SeedOrder();

        using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|pickup_failed");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Shipped);
    }

    [Fact]
    public async Task A_notice_for_an_order_the_seller_has_not_marked_shipped_asks_the_provider_to_try_again()
    {
        var order = SeedOrder(shipped: false);

        using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task A_notice_for_an_order_that_does_not_exist_is_acknowledged_so_the_provider_stops()
    {
        using var response = await PostAsync("fake", $"evt-{NewId()}|KLK-NOSUCH99|delivered");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_disputed_order_is_not_moved_by_a_delivery_notice()
    {
        var order = SeedOrder();
        order.OpenDispute("Barang tidak sesuai.", order.BuyerId, factory.Clock);
        factory.Orders.Seed(order);

        using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Disputed);
    }

    [Fact]
    public async Task When_the_signing_key_cannot_be_read_the_answer_is_503_so_the_provider_sends_it_again()
    {
        var order = SeedOrder();
        factory.DeliveryWebhook.KeyUnavailable = true;
        try
        {
            using var response = await PostAsync("fake", $"evt-{NewId()}|{order.OrderNumber}|delivered");

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter.ShouldNotBeNull();
            (await Reload(order)).EscrowState.ShouldBe(EscrowState.Shipped);
        }
        finally
        {
            factory.DeliveryWebhook.KeyUnavailable = false;
        }
    }

    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_anything_reads_it()
    {
        using var response = await PostAsync("fake", new string('x', (64 * 1024) + 10));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_provider_that_only_pushes_is_never_asked_by_the_sweep()
    {
        var order = SeedOrder();
        factory.Courier.CanPoll = false;
        factory.Courier.Asked.Clear();
        try
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<SweepOrchestrator>().RunAsync(CancellationToken.None);

            factory.Courier.Asked.ShouldNotContain(order.Id);
        }
        finally
        {
            factory.Courier.CanPoll = factory.Courier.IsConfigured;
        }
    }

    [Fact]
    public async Task Marking_an_order_shipped_tells_the_provider_and_a_provider_failure_does_not_undo_the_shipment()
    {
        var listing = TestData.ActiveListing();
        factory.Listings.Seed(listing);

        Order Paid()
        {
            var paid = Order.Create(
                TestData.BidderId, listing.SellerId, [new OrderLine(listing.Id, listing.Title, null, listing.Price, 1)],
                Address, Jne, factory.Clock);
            paid.ConfirmPayment(paid.GrandTotal, factory.Clock);
            factory.Orders.Seed(paid);
            return paid;
        }

        // Fresh proof and waybill for every shipment: one set cannot back two orders.
        object Body() => new
        {
            trackingNumber = "JX" + Guid.NewGuid().ToString("N")[..12],
            packedParcelPhotoKey = TestData.MediaKey(MediaPurpose.ShippingProof, listing.SellerId, "packed-" + Guid.NewGuid().ToString("N")),
            trackingLabelPhotoKey = TestData.MediaKey(MediaPurpose.ShippingProof, listing.SellerId, "label-" + Guid.NewGuid().ToString("N")),
            shippedOn = "2026-01-05",
        };

        using var client = factory.CreateClientAs(listing.SellerId, TestData.SellerUsername);

        var first = Paid();
        (await client.PostAsJsonAsync($"/api/orders/{first.Id}/shipment", Body())).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Courier.Registered.ShouldContain(first.OrderNumber);

        factory.Courier.FailRegistration = true;
        try
        {
            var second = Paid();
            (await client.PostAsJsonAsync($"/api/orders/{second.Id}/shipment", Body())).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await Reload(second)).EscrowState.ShouldBe(EscrowState.Shipped);
            factory.Courier.Registered.ShouldNotContain(second.OrderNumber);
        }
        finally
        {
            factory.Courier.FailRegistration = false;
        }
    }
}
