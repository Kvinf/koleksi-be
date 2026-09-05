using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoOrderRepository</c> against a real table.</summary>
public sealed class OrderRepositoryTests : DynamoTestBase
{
    [DynamoLocalFact]
    public async Task An_order_survives_the_round_trip()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);

        await Table.Orders.AddManyAsync([order], Ct);
        var stored = await Table.Orders.FindAsync(order.Id, Ct);

        stored.ShouldNotBeNull();
        stored.OrderNumber.ShouldBe(order.OrderNumber);
        stored.BuyerId.ShouldBe(order.BuyerId);
        stored.SellerId.ShouldBe(order.SellerId);
        stored.Lines.Count.ShouldBe(1);
        stored.Lines[0].UnitPrice.Rupiah.ShouldBe(order.Lines[0].UnitPrice.Rupiah);
        stored.ShipTo.RecipientName.ShouldBe(order.ShipTo.RecipientName);
        stored.Courier.CourierCode.ShouldBe(order.Courier.CourierCode);
        stored.EscrowState.ShouldBe(EscrowState.AwaitingPayment);
    }

    [DynamoLocalFact]
    public async Task An_order_is_findable_by_its_printed_order_number()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        var stored = await Table.Orders.FindByOrderNumberAsync(order.OrderNumber, Ct);

        stored.ShouldNotBeNull();
        stored.Id.ShouldBe(order.Id);
    }

    [DynamoLocalFact]
    public async Task An_unknown_order_number_resolves_to_nothing()
    {
        var stored = await Table.Orders.FindByOrderNumberAsync("KLK-000000", Ct);

        stored.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_checkout_spanning_two_shops_writes_both_orders_atomically()
    {
        var listingOne = Fixtures.ActiveListing(title: "PSA 10 Charizard Base Set Holo 1999");
        var listingTwo = Fixtures.ActiveListing(title: "Sealed Gunpla RG Nu Gundam Ver Ka", priceRupiah: 1_800_000);
        var orderOne = Fixtures.SampleOrder(listingOne);
        var orderTwo = Fixtures.SampleOrder(listingTwo);

        await Table.Orders.AddManyAsync([orderOne, orderTwo], Ct);

        (await Table.Orders.FindAsync(orderOne.Id, Ct)).ShouldNotBeNull();
        (await Table.Orders.FindAsync(orderTwo.Id, Ct)).ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task Confirming_payment_persists_through_an_update()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var stored = await Table.Orders.FindAsync(order.Id, Ct);
        stored.ShouldNotBeNull();
        stored.EscrowState.ShouldBe(EscrowState.Paid);
        stored.PaidAt.ShouldNotBeNull();
    }

    [DynamoLocalFact]
    public async Task A_stale_version_update_is_rejected()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Table.Orders.UpdateAsync(order, expectedVersion: 41, Ct));
    }

    [DynamoLocalFact]
    public async Task Orders_list_for_both_the_buyer_and_the_seller()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        var forSeller = await Table.Orders.ListForSellerAsync(listing.SellerId, null, 10, Ct);
        var forBuyer = await Table.Orders.ListForBuyerAsync(Fixtures.BidderId, null, 10, Ct);

        forSeller.Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        forBuyer.Items.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
    }

    [DynamoLocalFact]
    public async Task A_delivered_order_past_its_auto_release_deadline_is_due()
    {
        var listing = Fixtures.ActiveListing();
        var order = Fixtures.SampleOrder(listing);
        await Table.Orders.AddManyAsync([order], Ct);

        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE1234567890", "photo/parcel.jpg", "photo/label.jpg",
                DateOnly.FromDateTime(Fixtures.Clock.UtcNow.UtcDateTime)),
            listing.SellerId, Fixtures.Clock);
        order.MarkDelivered(Fixtures.Clock);

        // The stored row is still at whatever AddManyAsync wrote (version 0) — none of the
        // in-memory mutations above have been persisted yet, only accumulated on the
        // aggregate. One UpdateAsync writes and validates against that stored version.
        await Table.Orders.UpdateAsync(order, expectedVersion: 0, Ct);

        var due = await Table.Orders.ListDueForAutoReleaseAsync(
            Fixtures.Clock.UtcNow + Order.AutoReleaseAfterDelivery + TimeSpan.FromMinutes(1), 10, Ct);

        due.ShouldContain(candidate => candidate.Id == order.Id);
    }
}
