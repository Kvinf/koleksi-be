using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The limits that stop checkout being used to hold a catalogue hostage: how many unpaid orders a
/// buyer can have open, how long renewals can keep an item reserved, the per-user rate limit, and a
/// double submit returning the first result.
/// </summary>
/// <remarks>
/// A class of its own, so its factory (and the clock it moves) is not shared with tests that read
/// the time as a constant.
/// </remarks>
public sealed class CheckoutAbuseEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static string NewBuyerId() => Guid.NewGuid().ToString();

    private static object CheckoutBody(string listingId) => new
    {
        source = "FixedPrice",
        referenceId = listingId,
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

    private Listing SeedListing(string? sellerId = null)
    {
        var listing = TestData.ActiveListingBy(sellerId ?? TestData.SellerId, price: Money.FromRupiah(1_000_000));
        factory.Listings.Seed(listing);
        return listing;
    }

    /// <summary>An unpaid order for <paramref name="buyerId"/> as it would stand <paramref name="age"/> after creation.</summary>
    private Order SeedUnpaidOrder(string buyerId, Listing listing, TimeSpan age = default, bool paid = false)
    {
        var clock = new OffsetClock(factory.Clock, -age);
        var order = Order.Create(
            buyerId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
            FlatRateCouriers.Find("jne")!, clock);

        if (paid)
        {
            order.ConfirmPayment(order.GrandTotal, clock);
        }

        factory.Orders.Seed(order);
        return order;
    }

    private void SeedUnpaidOrders(string buyerId, int count, TimeSpan age = default, bool paid = false)
    {
        for (var i = 0; i < count; i++)
        {
            SeedUnpaidOrder(buyerId, SeedListing(), age, paid);
        }
    }

    // ─── Open unpaid orders ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_buyer_with_ten_unpaid_orders_cannot_start_another_checkout()
    {
        var buyerId = NewBuyerId();
        SeedUnpaidOrders(buyerId, PendingLimit);
        using var client = factory.CreateClientAs(buyerId, "abuse.pending");

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(SeedListing().Id));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.too_many_pending");
    }

    [Fact]
    public async Task The_tenth_unpaid_order_is_still_allowed()
    {
        var buyerId = NewBuyerId();
        SeedUnpaidOrders(buyerId, PendingLimit - 1);
        using var client = factory.CreateClientAs(buyerId, "abuse.ninth");

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(SeedListing().Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Checking_out_an_item_that_already_has_an_unpaid_order_replaces_it_instead_of_counting_twice()
    {
        var buyerId = NewBuyerId();
        var listing = SeedListing();
        SeedUnpaidOrder(buyerId, listing);
        SeedUnpaidOrders(buyerId, PendingLimit - 1);
        using var client = factory.CreateClientAs(buyerId, "abuse.retry");

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(listing.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lapsed_and_paid_orders_do_not_count_against_the_limit()
    {
        var buyerId = NewBuyerId();
        SeedUnpaidOrders(buyerId, PendingLimit, age: Order.PaymentWindow + TimeSpan.FromMinutes(5));
        SeedUnpaidOrders(buyerId, PendingLimit, paid: true);
        using var client = factory.CreateClientAs(buyerId, "abuse.lapsed");

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(SeedListing().Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_cart_checkout_counts_every_order_it_would_create_against_the_limit()
    {
        var buyerId = NewBuyerId();
        SeedUnpaidOrders(buyerId, PendingLimit - 1);
        var first = SeedListing(TestData.SellerId);
        var second = SeedListing(TestData.RivalId);
        using var client = factory.CreateClientAs(buyerId, "abuse.cart");

        using var response = await client.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id, second.Id },
            shipTo = new
            {
                recipientName = "Rangga Wijaya", phone = "081234567890", street = "Jl. Merdeka No. 1",
                city = "Bandung", province = "Jawa Barat", postalCode = "40111",
            },
            courierCodeByShop = new Dictionary<string, string>
            {
                [factory.PublicHandleOf(TestData.SellerId)] = "jne",
                [factory.PublicHandleOf(TestData.RivalId)] = "jne",
            },
        });

        // Nine open plus two new would be eleven.
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.too_many_pending");
    }

    private const int PendingLimit = 10;

    private static readonly string[] SingleBogusId = ["x"];

    // ─── Bounded hold renewal ────────────────────────────────────────────────────

    [Fact]
    public async Task Re_checking_out_every_few_minutes_cannot_keep_an_item_reserved_past_the_cap()
    {
        var listing = SeedListing();
        using var holder = factory.CreateClientAs(NewBuyerId(), "abuse.holder");
        using var rival = factory.CreateClientAs(NewBuyerId(), "abuse.rival");
        var firstTaken = factory.Clock.UtcNow;

        // Minutes 0, 14 and 28: each renewal would run another fifteen minutes, but the last is
        // cut to minute 30, thirty minutes after the hold was first taken.
        foreach (var minute in new[] { 0, 14, 28 })
        {
            factory.Clock.UtcNow = firstTaken + TimeSpan.FromMinutes(minute);
            (await holder.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(listing.Id)))
                .StatusCode.ShouldBe(HttpStatusCode.OK, $"minute {minute}");
        }

        factory.Clock.UtcNow = firstTaken + TimeSpan.FromMinutes(29);
        using var tooEarly = await rival.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(listing.Id));
        tooEarly.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooEarly.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.item_on_hold");

        factory.Clock.UtcNow = firstTaken + IListingHoldStore.MaxTotalDuration + TimeSpan.FromSeconds(1);
        using var afterCap = await rival.PostAsJsonAsync("/api/orders/checkout", CheckoutBody(listing.Id));
        afterCap.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_hold_store_reports_the_capped_expiry()
    {
        var holds = new FakeListingHoldStore(factory.Clock);
        var start = factory.Clock.UtcNow;

        var first = await holds.TryHoldAsync("L1", "buyer", "order-1", CancellationToken.None);
        first.HeldUntil.ShouldBe(start + IListingHoldStore.Duration);

        factory.Clock.UtcNow = start + TimeSpan.FromMinutes(14);
        var second = await holds.TryHoldAsync("L1", "buyer", "order-2", CancellationToken.None);
        second.HeldUntil.ShouldBe(start + TimeSpan.FromMinutes(29));

        factory.Clock.UtcNow = start + TimeSpan.FromMinutes(28);
        var third = await holds.TryHoldAsync("L1", "buyer", "order-3", CancellationToken.None);
        third.HeldUntil.ShouldBe(start + IListingHoldStore.MaxTotalDuration);
    }

    // ─── Rate limit ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Checkout_and_cart_checkout_share_one_per_user_budget()
    {
        using var client = factory.CreateClientAs(NewBuyerId(), "abuse.hammer");
        var statuses = new List<HttpStatusCode>();

        // An invalid source is refused by the endpoint, which is after the limiter has counted it.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await client.PostAsJsonAsync("/api/orders/checkout", new { source = "Nope" });
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldAllBe(status => status == HttpStatusCode.BadRequest);

        using var cart = await client.PostAsJsonAsync("/api/cart/checkout", new { listingIds = SingleBogusId });
        cart.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await cart.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("rate_limited");
    }

    [Fact]
    public async Task One_buyer_hammering_checkout_leaves_another_buyers_budget_alone()
    {
        using var noisy = factory.CreateClientAs(NewBuyerId(), "abuse.noisy");
        for (var attempt = 0; attempt < 12; attempt++)
        {
            (await noisy.PostAsJsonAsync("/api/orders/checkout", new { source = "Nope" })).Dispose();
        }

        using var quiet = factory.CreateClientAs(NewBuyerId(), "abuse.quiet");
        using var response = await quiet.PostAsJsonAsync("/api/orders/checkout", new { source = "Nope" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ─── Double submit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_double_submitted_checkout_returns_the_first_result_and_creates_one_order()
    {
        var buyerId = NewBuyerId();
        var listing = SeedListing();
        using var client = factory.CreateClientAs(buyerId, "abuse.double");
        var key = Guid.NewGuid().ToString();

        var first = await SubmitAsync(client, listing.Id, key);
        var second = await SubmitAsync(client, listing.Id, key);

        second.ShouldBe(first);
        (await factory.Orders.ListForBuyerAsync(buyerId, null, 20, CancellationToken.None)).Items.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Without_a_key_checkout_behaves_as_before()
    {
        var buyerId = NewBuyerId();
        var listing = SeedListing();
        using var client = factory.CreateClientAs(buyerId, "abuse.nokey");

        var first = await SubmitAsync(client, listing.Id, key: null);
        var second = await SubmitAsync(client, listing.Id, key: null);

        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task A_key_used_again_after_its_retention_has_run_out_does_the_work_again()
    {
        var buyerId = NewBuyerId();
        var listing = SeedListing();
        using var client = factory.CreateClientAs(buyerId, "abuse.expiry");
        var key = Guid.NewGuid().ToString();

        var first = await SubmitAsync(client, listing.Id, key);
        factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var later = await SubmitAsync(client, listing.Id, key);

        // Before expiry was enforced, the old result was replayed here for as long as the table's
        // lazy TTL sweep took to get to it.
        later.ShouldNotBe(first);
    }

    private static async Task<string> SubmitAsync(HttpClient client, string listingId, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout")
        {
            Content = JsonContent.Create(CheckoutBody(listingId)),
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.ReadJsonAsync()).GetProperty("orderId").GetString()!;
    }

    /// <summary>The host clock, shifted: how old an order looks when it was made earlier.</summary>
    private sealed class OffsetClock(IClock inner, TimeSpan offset) : IClock
    {
        public DateTimeOffset UtcNow => inner.UtcNow + offset;
    }
}
