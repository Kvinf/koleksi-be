using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// The per-person dispute pointers: a dispute is findable from each person in it by one query on one partition, written in the same
/// transaction that gives the order its dispute. Proved here by the shape of what is sent; DynamoDB's behaviour is in
/// <c>DisputePointerTests</c> (DynamoDB Local).
/// </summary>
public sealed class DisputePointerShapeTests
{
    private static readonly DynamoOptions Options = new() { TableName = "koleksi-test", ServiceUrl = "http://localhost:8000" };

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));

    private static Order Disputed()
    {
        var order = Order.Create(
            "buyer-sub", "seller-sub",
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            Clock);
        order.ConfirmPayment(order.GrandTotal, Clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), "seller-sub", Clock);
        order.MarkDelivered(Clock);
        order.OpenDispute("Kartu bukan PSA 10.", "buyer-sub", Clock);
        return order;
    }

    [Fact]
    public void A_dispute_leaves_one_pointer_for_each_person_in_it_in_their_own_partition()
    {
        var order = Disputed();

        var pointers = DynamoOrderRepository.DisputePointers(order).ToList();

        pointers.Select(p => p[DynamoTable.Pk].S).ToList().ShouldBe([DynamoTable.UserDisputePk("buyer-sub"), DynamoTable.UserDisputePk("seller-sub")]);
        pointers.ShouldAllBe(p => p[DynamoTable.Sk].S == DynamoTable.UserDisputeSk(order.Id) && p["orderId"].S == order.Id);
        DynamoTable.UserDisputePk("buyer-sub").ShouldNotBe(DynamoTable.UserDisputePk("seller-sub"));
    }

    [Fact]
    public void A_pointer_partition_can_never_be_an_order_or_other_partition()
    {
        DynamoTable.UserDisputePk("x").ShouldStartWith("UDISPUTE#");
        DynamoTable.UserDisputePk("x").ShouldNotBe(DynamoTable.OrderPk("x"));
    }

    [Fact]
    public async Task Updating_an_order_with_a_dispute_is_one_transaction_of_the_order_and_both_pointers()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var order = Disputed();

        await new DynamoOrderRepository(client, Options).UpdateAsync(order, order.Version, CancellationToken.None);

        recorder.Writes.ShouldBe(1);
        var items = recorder.Transactions.ShouldHaveSingleItem().TransactItems;
        items.Count.ShouldBe(3);

        var orderWrite = items[0].Put.ShouldNotBeNull();
        orderWrite.Item[DynamoTable.Pk].S.ShouldBe(DynamoTable.OrderPk(order.Id));
        orderWrite.ConditionExpression.ShouldBe("#v = :expected", "the pointers never go in unless the order's own write is allowed");

        items.Skip(1).Select(i => i.Put.ShouldNotBeNull().Item[DynamoTable.Pk].S).ToList()
            .ShouldBe([DynamoTable.UserDisputePk("buyer-sub"), DynamoTable.UserDisputePk("seller-sub")]);
    }

    [Fact]
    public async Task Finding_a_persons_disputes_is_one_query_on_their_pointer_partition_and_never_a_scan()
    {
        var recorder = DynamoRecorder.Create(out var client);

        var found = await new DynamoOrderRepository(client, Options).ListDisputedForUserAsync("buyer-sub", CancellationToken.None);

        found.ShouldBeEmpty();
        recorder.Scans.ShouldBe(0);
        var query = recorder.Queries.ShouldHaveSingleItem();
        query.IndexName.ShouldBeNull("the pointers are on the table's own key, not an index");
        query.ExpressionAttributeValues[":pk"].S.ShouldBe(DynamoTable.UserDisputePk("buyer-sub"));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
