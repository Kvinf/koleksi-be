using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>The shared test host, with the simulators switched on, as they are on a developer's machine.</summary>
public sealed class DevelopmentApiFactory : KoleksiApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);
        Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseEnvironment(builder, "Development");
        builder.UseSetting("Koleksi:Simulators:Enabled", "true");
    }
}

/// <summary>
/// The simulator stands in for the payment gateway and the courier, and for nothing else: it can only do what
/// the real notification could, only for a party to the order, and it never skips a rule the real path has.
/// </summary>
public sealed class SimulatorEndpointTests(DevelopmentApiFactory factory) : IClassFixture<DevelopmentApiFactory>
{
    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), EstimatedDays: 3);

    private static readonly ShippingAddress Address =
        new("Dwi Prasetyo", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810");

    private static string NewId() => Guid.NewGuid().ToString();

    private Order SeedOrder(string buyer, string seller, bool paid = false, bool shipped = false)
    {
        var order = Order.Create(
            buyer, seller, [new OrderLine(NewId(), "Kartu uji", null, Money.FromRupiah(1_000_000), 1)],
            Address, Jne, factory.Clock);
        if (paid || shipped)
        {
            order.ConfirmPayment(order.GrandTotal, factory.Clock);
        }

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

    private async Task<Order> Reload(Order order) =>
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!;

    private static Task<HttpResponseMessage> Courier(HttpClient client, Order order, string state) =>
        client.PostAsJsonAsync($"/api/dev/simulator/orders/{order.Id}/courier", new { state });

    [Fact]
    public async Task The_buyer_can_simulate_the_payment_and_the_order_becomes_paid()
    {
        var buyer = NewId();
        var order = SeedOrder(buyer, NewId());
        using var client = factory.CreateClientAs(buyer, "sim.buyer");

        using var response = await client.PostAsync($"/api/dev/simulator/orders/{order.Id}/payment", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task Simulating_the_payment_twice_changes_nothing_the_second_time()
    {
        var buyer = NewId();
        var order = SeedOrder(buyer, NewId());
        using var client = factory.CreateClientAs(buyer, "sim.twice");

        (await client.PostAsync($"/api/dev/simulator/orders/{order.Id}/payment", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var version = (await Reload(order)).Version;
        (await client.PostAsync($"/api/dev/simulator/orders/{order.Id}/payment", null)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Reload(order)).Version.ShouldBe(version);
    }

    [Fact]
    public async Task Only_the_buyer_can_simulate_paying_and_a_stranger_gets_the_answer_a_missing_order_gets()
    {
        var seller = NewId();
        var order = SeedOrder(NewId(), seller);
        using var sellerClient = factory.CreateClientAs(seller, "sim.seller");
        using var stranger = factory.CreateClientAs(NewId(), "sim.stranger");

        using var bySeller = await sellerClient.PostAsync($"/api/dev/simulator/orders/{order.Id}/payment", null);
        using var byStranger = await stranger.PostAsync($"/api/dev/simulator/orders/{order.Id}/payment", null);
        using var missing = await stranger.PostAsync($"/api/dev/simulator/orders/{NewId()}/payment", null);

        bySeller.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        byStranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await byStranger.Content.ReadAsStringAsync()).ShouldBe(await missing.Content.ReadAsStringAsync());
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [Fact]
    public async Task The_courier_cannot_report_a_parcel_that_has_not_been_shipped()
    {
        var buyer = NewId();
        var paid = SeedOrder(buyer, NewId(), paid: true);
        var unpaid = SeedOrder(buyer, NewId());
        using var client = factory.CreateClientAs(buyer, "sim.early");

        using var forPaid = await Courier(client, paid, "delivered");
        using var forUnpaid = await Courier(client, unpaid, "delivered");

        forPaid.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        forUnpaid.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Reload(paid)).EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task The_courier_moves_a_shipped_order_to_in_transit_then_delivered_and_the_two_day_clock_starts()
    {
        var seller = NewId();
        var order = SeedOrder(NewId(), seller, shipped: true);
        using var client = factory.CreateClientAs(seller, "sim.courier");

        (await Courier(client, order, "in_transit")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.InTransit);

        (await Courier(client, order, "delivered")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await Reload(order);
        after.EscrowState.ShouldBe(EscrowState.Delivered);
        after.AutoReleaseAt.ShouldBe(factory.Clock.UtcNow + Order.AutoReleaseAfterDelivery);
    }

    [Fact]
    public async Task A_stranger_cannot_move_someone_elses_parcel_and_an_unknown_state_is_refused()
    {
        var buyer = NewId();
        var order = SeedOrder(buyer, NewId(), shipped: true);
        using var stranger = factory.CreateClientAs(NewId(), "sim.stranger.courier");
        using var party = factory.CreateClientAs(buyer, "sim.party");

        (await Courier(stranger, order, "delivered")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Courier(party, order, "teleported")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Reload(order)).EscrowState.ShouldBe(EscrowState.Shipped);
    }

    [Fact]
    public async Task The_list_shows_only_the_callers_orders_and_what_each_button_may_do()
    {
        var buyer = NewId();
        var seller = NewId();
        var awaiting = SeedOrder(buyer, seller);
        var paid = SeedOrder(buyer, seller, paid: true);
        var shipped = SeedOrder(buyer, seller, shipped: true);
        var someoneElses = SeedOrder(NewId(), NewId());
        using var buyerClient = factory.CreateClientAs(buyer, "sim.list.buyer");
        using var sellerClient = factory.CreateClientAs(seller, "sim.list.seller");

        var asBuyer = (await (await buyerClient.GetAsync("/api/dev/simulator")).ReadJsonAsync()).GetProperty("orders");
        var asSeller = (await (await sellerClient.GetAsync("/api/dev/simulator")).ReadJsonAsync()).GetProperty("orders");

        System.Text.Json.JsonElement Row(System.Text.Json.JsonElement rows, Order order) =>
            rows.EnumerateArray().Single(r => r.GetProperty("id").GetString() == order.Id);

        Row(asBuyer, awaiting).GetProperty("canPay").GetBoolean().ShouldBeTrue();
        Row(asBuyer, paid).GetProperty("canPay").GetBoolean().ShouldBeFalse();
        Row(asBuyer, shipped).GetProperty("canMoveCourier").GetBoolean().ShouldBeTrue();
        Row(asBuyer, shipped).GetProperty("canConfirm").GetBoolean().ShouldBeTrue();
        Row(asSeller, awaiting).GetProperty("canPay").GetBoolean().ShouldBeFalse();
        Row(asSeller, paid).GetProperty("needsSellerToShip").GetBoolean().ShouldBeTrue();
        asBuyer.EnumerateArray().Any(r => r.GetProperty("id").GetString() == someoneElses.Id).ShouldBeFalse();
        asSeller.EnumerateArray().Any(r => r.GetProperty("id").GetString() == someoneElses.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task The_simulator_needs_a_signed_in_caller()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/dev/simulator")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync($"/api/dev/simulator/orders/{NewId()}/payment", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/dev/simulator/sweep", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_sweep_button_runs_the_real_sweep_and_reports_what_it_did()
    {
        using var client = factory.CreateClientAs(NewId(), "sim.sweep");

        using var response = await client.PostAsync("/api/dev/simulator/sweep", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("errors").GetInt32().ShouldBe(0);
    }
}


/// <summary>A deployed dev stage: not a developer's machine, but the stage says it is dev and the flag is on.</summary>
public sealed class DevStageApiFactory : KoleksiApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);
        builder.UseSetting("Koleksi:Stage", "dev");
        builder.UseSetting("Koleksi:Simulators:Enabled", "true");
    }
}

/// <summary>The same flag on any other stage: the host refuses to start.</summary>
public sealed class ProdStageApiFactory : KoleksiApiFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);
        builder.UseSetting("Koleksi:Stage", "prod");
        builder.UseSetting("Koleksi:Simulators:Enabled", "true");
    }
}

/// <summary>Where the simulators are not switched on, or must not be, they do not exist.</summary>
public sealed class SimulatorSwitchTests
{
    [Fact]
    public async Task Without_the_switch_every_simulator_route_is_a_404_for_a_signed_in_caller()
    {
        using var factory = new KoleksiApiFactory();
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "sim.absent");
        var id = Guid.NewGuid().ToString();

        (await client.GetAsync("/api/dev/simulator")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync($"/api/dev/simulator/orders/{id}/payment", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync($"/api/dev/simulator/orders/{id}/courier", new { state = "delivered" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/dev/simulator/sweep", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task On_the_dev_stage_the_switch_turns_them_on_without_a_Development_environment()
    {
        using var factory = new DevStageApiFactory();
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "sim.devstage");

        (await client.GetAsync("/api/dev/simulator")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void On_any_other_stage_the_host_refuses_to_start_with_the_switch_on()
    {
        using var factory = new ProdStageApiFactory();

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        var text = failure.ToString();
        text.ShouldContain("Simulators:Enabled");
    }
}
