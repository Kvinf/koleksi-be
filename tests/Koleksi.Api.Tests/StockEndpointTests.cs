using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Stock: every non-auction listing has units, checkout reserves them first, a reservation lasts fifteen
/// minutes (thirty for an auction win) and an unpaid one gives its units back.
/// </summary>
public sealed class StockEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static object CheckoutBody(string source, string referenceId, int? quantity = null) => new
    {
        source,
        referenceId,
        quantity,
        shipTo = new
        {
            recipientName = "Rangga Wijaya", phone = "081234567890", street = "Jl. Merdeka No. 1",
            city = "Bandung", province = "Jawa Barat", postalCode = "40111",
        },
        courierCode = "jne",
    };

    private Listing SeedListing(int stock)
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        listing.SetStock(stock, new MutableClock());
        factory.Listings.Seed(listing);
        return listing;
    }

    private HttpClient Buyer(string name) => factory.CreateClientAs(Guid.NewGuid().ToString(), name);

    private static async Task<string> CodeOf(HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).GetProperty("code").GetString()!;

    [Fact]
    public async Task Different_buyers_can_each_reserve_units_until_none_are_free()
    {
        var listing = SeedListing(stock: 3);
        using var a = Buyer("stock.a");
        using var b = Buyer("stock.b");
        using var c = Buyer("stock.c");

        (await a.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 2))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await b.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Three units, all reserved: the next buyer is refused, and told it may come back.
        using var refused = await c.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1));
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(refused)).ShouldBe("order.item_on_hold");
    }

    [Fact]
    public async Task Asking_for_more_than_is_free_is_refused_and_takes_nothing()
    {
        var listing = SeedListing(stock: 3);
        using var a = Buyer("stock.greedy.a");
        using var b = Buyer("stock.greedy.b");
        (await a.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 2))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var tooMany = await b.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 2));

        tooMany.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var usage = await factory.Holds.GetAvailabilityAsync(listing.Id, listing.Stock, CancellationToken.None);
        usage.Held.ShouldBe(2);
        usage.Available.ShouldBe(1);
    }

    [Fact]
    public async Task A_quantity_above_what_the_seller_stocked_is_refused()
    {
        var listing = SeedListing(stock: 2);
        using var buyer = Buyer("stock.over");

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 3));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("order.invalid_quantity");
    }

    [Fact]
    public async Task A_quantity_of_zero_is_refused()
    {
        var listing = SeedListing(stock: 2);
        using var buyer = Buyer("stock.zero");

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 0));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("order.invalid_quantity");
    }

    [Fact]
    public async Task The_order_total_scales_with_the_units_bought()
    {
        var listing = SeedListing(stock: 5);
        using var buyer = Buyer("stock.total");

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 3));
        var orderId = (await response.ReadJsonAsync()).GetProperty("orderId").GetString()!;

        var order = await factory.Orders.FindAsync(orderId, CancellationToken.None);
        order!.Lines.Single().Quantity.ShouldBe(3);
        order.ItemsTotal.ShouldBe(Money.FromRupiah(9_000_000));
    }

    [Fact]
    public async Task An_unpaid_reservation_gives_its_units_back_after_fifteen_minutes()
    {
        var listing = SeedListing(stock: 1);
        using var first = Buyer("stock.lapse.first");
        using var second = Buyer("stock.lapse.second");
        (await first.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id))).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Clock.UtcNow += TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59);
        try
        {
            (await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
                .StatusCode.ShouldBe(HttpStatusCode.Conflict);

            factory.Clock.UtcNow += TimeSpan.FromSeconds(2);
            (await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_frees_its_units_at_once()
    {
        var listing = SeedListing(stock: 1);
        using var first = Buyer("stock.cancel.first");
        using var second = Buyer("stock.cancel.second");
        using var checkout = await first.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
        var orderId = (await checkout.ReadJsonAsync()).GetProperty("orderId").GetString()!;

        (await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await first.PostAsync($"/api/orders/{orderId}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await second.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Payment_sells_the_units_and_the_listing_stays_on_sale_until_the_last_one_is_gone()
    {
        var listing = SeedListing(stock: 2);
        var buyerA = Guid.NewGuid().ToString();
        var buyerB = Guid.NewGuid().ToString();
        using var a = factory.CreateClientAs(buyerA, "stock.pay.a");
        using var b = factory.CreateClientAs(buyerB, "stock.pay.b");

        var orderA = await CheckoutAndPayAsync(a, listing);

        var afterFirst = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        afterFirst!.Status.ShouldBe(ListingStatus.Active);
        var usage = await factory.Holds.GetAvailabilityAsync(listing.Id, listing.Stock, CancellationToken.None);
        usage.Sold.ShouldBe(1);
        usage.Available.ShouldBe(1);
        (await factory.Orders.FindAsync(orderA, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);

        await CheckoutAndPayAsync(b, listing);

        var afterLast = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        afterLast!.Status.ShouldBe(ListingStatus.Sold);
        (await factory.Holds.GetAvailabilityAsync(listing.Id, listing.Stock, CancellationToken.None)).SoldOut.ShouldBeTrue();
    }

    [Fact]
    public async Task A_sold_unit_is_never_given_back_when_someone_elses_hold_lapses()
    {
        var listing = SeedListing(stock: 1);
        using var buyer = Buyer("stock.sold.forever");
        await CheckoutAndPayAsync(buyer, listing);

        factory.Clock.UtcNow += TimeSpan.FromHours(5);
        try
        {
            using var late = Buyer("stock.sold.late");
            (await late.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id)))
                .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task The_item_page_reports_how_many_units_are_free_right_now()
    {
        var listing = SeedListing(stock: 3);
        using var buyer = Buyer("stock.page");
        (await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 2))).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var anonymous = factory.CreateClient();
        using var page = await anonymous.GetAsync($"/api/catalog/listings/x-{listing.Id}");
        var stock = (await page.ReadJsonAsync()).GetProperty("stock");

        stock.GetProperty("total").GetInt32().ShouldBe(3);
        stock.GetProperty("available").GetInt32().ShouldBe(1);
        stock.GetProperty("held").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task The_sellers_dashboard_list_says_how_many_units_are_left_not_only_how_many_were_listed()
    {
        var listing = SeedListing(stock: 3);
        using var buyer = Buyer("stock.dash");
        (await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 2))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var seller = factory.CreateClientAs(listing.SellerId, "stock.dash.seller");

        var mine = await (await seller.GetAsync("/api/listings/mine")).ReadJsonAsync();
        var row = mine.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == listing.Id);

        row.GetProperty("stock").GetInt32().ShouldBe(3);
        row.GetProperty("stockAvailable").GetInt32().ShouldBe(1);
        row.GetProperty("stockHeld").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_dashboard_with_many_multi_unit_listings_answers_every_row_with_its_own_count()
    {
        // Twelve live multi-unit listings of one seller (more than the lookups run at once), with a different number held on each.
        var sellerId = Guid.NewGuid().ToString();
        var expected = new Dictionary<string, (int Stock, int Held)>(StringComparer.Ordinal);
        using var buyer = Buyer("stock.dash.many.buyer");
        for (var i = 0; i < 12; i++)
        {
            var listing = TestData.ActiveListingBy(sellerId, title: $"Gunpla RG nomor {i}", price: Money.FromRupiah(500_000));
            listing.SetStock(4 + (i % 3), new MutableClock());
            factory.Listings.Seed(listing);
            var held = i % 3;
            if (held > 0)
            {
                (await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, held))).StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            expected[listing.Id] = (listing.Stock, held);
        }

        using var seller = factory.CreateClientAs(sellerId, "stock.dash.many.seller");
        var mine = await (await seller.GetAsync("/api/listings/mine")).ReadJsonAsync();

        var rows = mine.GetProperty("items").EnumerateArray().ToList();
        rows.Count.ShouldBe(12);
        foreach (var row in rows)
        {
            var (stock, held) = expected[row.GetProperty("id").GetString()!];
            row.GetProperty("stockAvailable").GetInt32().ShouldBe(stock - held);
            row.GetProperty("stockHeld").GetInt32().ShouldBe(held);
        }
    }

    [Fact]
    public async Task A_single_unit_listing_has_no_available_count_on_the_dashboard_list()
    {
        var listing = SeedListing(stock: 1);
        using var seller = factory.CreateClientAs(listing.SellerId, "stock.dash.single");

        var mine = await (await seller.GetAsync("/api/listings/mine")).ReadJsonAsync();
        var row = mine.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == listing.Id);

        // Null is left out of the JSON altogether; the dashboard reads absent and null the same way.
        (!row.TryGetProperty("stockAvailable", out var available) || available.ValueKind == System.Text.Json.JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task An_auction_win_is_due_thirty_minutes_after_the_close_and_is_refused_after_that()
    {
        var (listing, auction) = SeedEndedAuction(out var winnerId);
        using var winner = factory.CreateClientAs(winnerId, "stock.auction.winner");

        // 29 minutes after the close: allowed, and the order inherits the close's deadline, not a fresh one.
        factory.Clock.UtcNow = auction.ClosedAt!.Value + TimeSpan.FromMinutes(29);
        try
        {
            using var inTime = await winner.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AuctionWin", listing.Id));
            inTime.StatusCode.ShouldBe(HttpStatusCode.OK);
            var orderId = (await inTime.ReadJsonAsync()).GetProperty("orderId").GetString()!;
            var order = await factory.Orders.FindAsync(orderId, CancellationToken.None);
            order!.Source.ShouldBe(OrderSource.AuctionWin);
            order.PaymentDueAt.ShouldBe(auction.ClosedAt.Value + TimeSpan.FromMinutes(30));
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task An_auction_winner_who_comes_after_thirty_minutes_is_refused()
    {
        var (listing, auction) = SeedEndedAuction(out var winnerId);
        using var winner = factory.CreateClientAs(winnerId, "stock.auction.late");

        factory.Clock.UtcNow = auction.ClosedAt!.Value + TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1);
        try
        {
            using var late = await winner.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AuctionWin", listing.Id));

            late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOf(late)).ShouldBe("auction.payment_window_closed");
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    [Fact]
    public async Task An_accepted_offer_checks_out_one_unit_and_cannot_ask_for_more()
    {
        var listing = SeedListing(stock: 3);
        using var buyer = Buyer("stock.offer");

        using var response = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AcceptedOffer", "does-not-matter", 2));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("order.invalid_quantity");
        _ = listing;
    }

    [Fact]
    public void An_auction_is_always_one_unit_and_a_seller_cannot_change_that()
    {
        var auction = TestData.ActiveListing(format: ListingFormat.Auction);

        auction.Stock.ShouldBe(1);
        Should.Throw<DomainException>(() => auction.SetStock(2, new MutableClock())).Code.ShouldBe("listing.auction_stock_fixed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void Stock_outside_one_to_ninety_nine_is_refused(int stock)
    {
        var listing = TestData.ActiveListing();

        Should.Throw<DomainException>(() => listing.SetStock(stock, new MutableClock())).Code.ShouldBe("listing.invalid_stock");
    }

    [Fact]
    public async Task A_seller_cannot_lower_the_stock_below_what_is_sold_or_being_paid_for()
    {
        var listing = SeedListing(stock: 4);
        using var buyer = Buyer("stock.lower");
        (await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 3))).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var seller = factory.CreateClientAs(listing.SellerId, "stock.lower.seller");

        using var refused = await seller.PutAsJsonAsync($"/api/listings/{listing.Id}", EditBody(listing, stock: 2));
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(refused)).ShouldBe("listing.stock_below_committed");

        using var allowed = await seller.PutAsJsonAsync($"/api/listings/{listing.Id}", EditBody(listing, stock: 3));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.Listings.FindAsync(listing.Id, CancellationToken.None))!.Stock.ShouldBe(3);
    }

    private static object EditBody(Listing listing, int stock) => new
    {
        categoryId = listing.CategoryId,
        title = listing.Title,
        priceRupiah = listing.Price.Rupiah,
        format = listing.Format.ToString(),
        condition = listing.Condition,
        city = listing.City,
        description = listing.Description,
        photos = listing.Photos.Select(photo => new { photo.StorageKey, photo.Width, photo.Height }),
        publish = true,
        stock,
    };

    private async Task<string> CheckoutAndPayAsync(HttpClient buyer, Listing listing)
    {
        using var checkout = await buyer.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));
        checkout.StatusCode.ShouldBe(HttpStatusCode.OK);
        var orderId = (await checkout.ReadJsonAsync()).GetProperty("orderId").GetString()!;
        var order = (await factory.Orders.FindAsync(orderId, CancellationToken.None))!;

        factory.Payments.NextNotification = new PaymentNotification(
            order.OrderNumber, PaymentStatus.Settled, order.GrandTotal, $"PROC-{order.Id}", $"evt-{order.Id}");
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/payments/webhook", new { })).StatusCode.ShouldBe(HttpStatusCode.OK);
        return orderId;
    }

    private (Listing Listing, Auction Auction) SeedEndedAuction(out string winnerId)
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);
        factory.Listings.Seed(listing);
        var auction = TestData.LiveAuction(listing, Money.FromRupiah(1_000_000));
        winnerId = Guid.NewGuid().ToString();
        auction.PlaceBid(winnerId, "winner", Money.FromRupiah(1_100_000), factory.Clock);
        factory.Clock.UtcNow = auction.EndsAt + TimeSpan.FromSeconds(1);
        auction.Close(factory.Clock);
        factory.Clock.UtcNow = MutableClock.Origin;
        factory.Auctions.Seed(auction);
        return (listing, auction);
    }

    [Fact]
    public async Task One_buyer_cannot_keep_an_item_off_the_shelf_by_re_checking_out_every_fifteen_minutes()
    {
        var listing = SeedListing(stock: 1);
        using var hoarder = Buyer("stock.hoarder");
        using var honest = Buyer("stock.honest");
        try
        {
            for (var attempt = 1; attempt <= PendingOrderLimitAttempts; attempt++)
            {
                (await hoarder.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1)))
                    .StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt}");
                factory.Clock.UtcNow += Order.PaymentWindow + TimeSpan.FromSeconds(1);
            }

            // The fourth attempt inside two hours is refused ...
            using var refused = await hoarder.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1));
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOf(refused)).ShouldBe("order.too_many_attempts");

            // ... so the item is free for someone who means to pay.
            (await honest.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // And the hoarder is let back in once the attempts are more than two hours old.
            factory.Clock.UtcNow += TimeSpan.FromHours(3);
            using var later = Buyer("stock.hoarder.later");
            (await later.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id, 1)))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Clock.UtcNow = MutableClock.Origin;
        }
    }

    private const int PendingOrderLimitAttempts = 3;
}
