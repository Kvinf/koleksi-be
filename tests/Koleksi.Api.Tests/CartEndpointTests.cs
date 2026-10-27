using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The cart and cart checkout, end to end: how shops are named on the wire, and how a courier
/// choice finds its way back to the right seller without an account id ever being sent.
/// </summary>
public sealed class CartEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly Regex AccountIdShape = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);

    private static void ShouldCarryNoAccountIdentity(string wire)
    {
        foreach (var id in new[] { TestData.SellerId, TestData.RivalId, TestData.BidderId })
        {
            wire.ShouldNotContain(id, Case.Insensitive);
        }

        wire.ShouldNotContain("@");
        AccountIdShape.IsMatch(wire).ShouldBeFalse("a Cognito-subject-shaped value is on the wire");
        wire.ShouldNotContain("sellerId", Case.Insensitive);
        wire.ShouldNotContain("buyerId", Case.Insensitive);
    }

    private static object ShipTo() => new
    {
        recipientName = "Rangga Wijaya",
        phone = "081234567890",
        street = "Jl. Merdeka No. 1",
        city = "Bandung",
        province = "Jawa Barat",
        postalCode = "40111",
    };

    private (Listing FromSeller, Listing FromRival) SeedTwoShops()
    {
        var first = TestData.ActiveListingBy(TestData.SellerId, "PSA 10 Charizard", Money.FromRupiah(3_000_000));
        var second = TestData.ActiveListingBy(TestData.RivalId, "Gunpla RG Nu Gundam", Money.FromRupiah(1_200_000));
        factory.Listings.Seed(first);
        factory.Listings.Seed(second);
        return (first, second);
    }

    private static string NewBuyerId() => Guid.NewGuid().ToString();

    [Fact]
    public async Task The_cart_names_each_shop_by_handle_and_carries_no_account_id()
    {
        var (first, second) = SeedTwoShops();
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.reader");
        (await buyer.PostAsJsonAsync("/api/cart/items", new { listingId = first.Id })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await buyer.PostAsJsonAsync("/api/cart/items", new { listingId = second.Id })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await buyer.GetAsync("/api/cart");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.ReadTextAsync();
        ShouldCarryNoAccountIdentity(text);

        var rows = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        rows.Single(row => row.GetProperty("listingId").GetString() == first.Id)
            .GetProperty("shopHandle").GetString().ShouldBe(factory.PublicHandleOf(TestData.SellerId));
        rows.Single(row => row.GetProperty("listingId").GetString() == second.Id)
            .GetProperty("shopHandle").GetString().ShouldBe(factory.PublicHandleOf(TestData.RivalId));
    }

    [Fact]
    public async Task Checkout_matches_each_courier_pick_to_its_shop_by_handle()
    {
        var (first, second) = SeedTwoShops();
        var buyerId = NewBuyerId();
        using var buyer = factory.CreateClientAs(buyerId, "cart.buyer.handles");
        var handleA = factory.PublicHandleOf(TestData.SellerId);
        var handleB = factory.PublicHandleOf(TestData.RivalId);

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id, second.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string> { [handleA] = "jne", [handleB] = "sicepat" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.ReadTextAsync();
        ShouldCarryNoAccountIdentity(text);

        var returned = JsonDocument.Parse(text).RootElement.GetProperty("orders").EnumerateArray()
            .Select(order => order.GetProperty("shopHandle").GetString()).ToList();
        returned.ShouldBe([handleA, handleB], ignoreOrder: true);

        var created = (await factory.Orders.ListForBuyerAsync(buyerId, null, 20, CancellationToken.None)).Items;
        created.Count.ShouldBe(2);
        created.Single(order => order.SellerId == TestData.SellerId).Courier.CourierCode.ShouldBe("jne");
        created.Single(order => order.SellerId == TestData.RivalId).Courier.CourierCode.ShouldBe("sicepat");
    }

    [Fact]
    public async Task An_unknown_shop_handle_in_the_courier_map_is_refused_and_nothing_is_created()
    {
        var (first, _) = SeedTwoShops();
        var buyerId = NewBuyerId();
        using var buyer = factory.CreateClientAs(buyerId, "cart.buyer.ghost");

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string>
            {
                [factory.PublicHandleOf(TestData.SellerId)] = "jne",
                ["toko-hantu"] = "jne",
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.invalid_courier");
        (await factory.Orders.ListForBuyerAsync(buyerId, null, 20, CancellationToken.None)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_courier_map_keyed_by_account_id_no_longer_selects_a_shop()
    {
        var (first, _) = SeedTwoShops();
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.buyer.oldkey");

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string> { [TestData.SellerId] = "jne" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.invalid_courier");
    }

    [Fact]
    public async Task A_shop_with_no_courier_pick_is_refused()
    {
        var (first, second) = SeedTwoShops();
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.buyer.partial");

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id, second.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string> { [factory.PublicHandleOf(TestData.SellerId)] = "jne" },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.invalid_courier");
    }

    [Fact]
    public async Task A_shop_whose_item_just_sold_may_still_be_named_in_the_courier_map()
    {
        // The buyer's screen was loaded before the other shop's item sold; naming its courier is
        // not a mistake worth failing the whole checkout over.
        var (first, second) = SeedTwoShops();
        second.MarkSold(factory.Clock);
        factory.Listings.Seed(second);
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.buyer.sold");

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id, second.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string>
            {
                [factory.PublicHandleOf(TestData.SellerId)] = "jne",
                [factory.PublicHandleOf(TestData.RivalId)] = "jne",
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task More_than_twenty_items_in_one_checkout_is_refused()
    {
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.buyer.many");

        var ids = Enumerable.Range(0, 21).Select(_ => TestData.UnknownListingId).ToArray();
        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = ids,
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("cart.too_many_items");
    }

    [Fact]
    public async Task A_checkout_without_a_ship_to_is_a_bad_request_not_a_server_error()
    {
        var (first, _) = SeedTwoShops();
        using var buyer = factory.CreateClientAs(NewBuyerId(), "cart.buyer.noaddress");

        using var response = await buyer.PostAsJsonAsync("/api/cart/checkout", new
        {
            listingIds = new[] { first.Id },
            courierCodeByShop = new Dictionary<string, string>(),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("checkout.invalid_details");
    }

    [Fact]
    public async Task A_double_submitted_cart_checkout_returns_the_first_result_instead_of_a_second_set_of_orders()
    {
        var (first, _) = SeedTwoShops();
        var buyerId = NewBuyerId();
        using var buyer = factory.CreateClientAs(buyerId, "cart.buyer.twice");
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            listingIds = new[] { first.Id },
            shipTo = ShipTo(),
            courierCodeByShop = new Dictionary<string, string> { [factory.PublicHandleOf(TestData.SellerId)] = "jne" },
        };

        var orderIds = new List<string>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/cart/checkout")
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await buyer.SendAsync(request);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt}");
            orderIds.Add((await response.ReadJsonAsync()).GetProperty("orders")[0].GetProperty("orderId").GetString()!);
        }

        orderIds[1].ShouldBe(orderIds[0]);
        (await factory.Orders.ListForBuyerAsync(buyerId, null, 20, CancellationToken.None)).Items.Count.ShouldBe(1);
    }
}
