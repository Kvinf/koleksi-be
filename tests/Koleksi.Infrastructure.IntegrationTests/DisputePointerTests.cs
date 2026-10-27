using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// A person's disputes found from a pointer row written with each dispute: one query on one partition, then batched gets of those
/// orders. Against a real table, because what matters is what DynamoDB does with the transaction and the batch.
/// </summary>
public sealed class DisputePointerTests : DynamoTestBase
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private DynamoOptions Options => new() { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl };

    private async Task<Order> Delivered(string buyer = Buyer, string seller = Seller)
    {
        var order = Order.Create(
            buyer, seller,
            [new OrderLine("L-" + Guid.NewGuid().ToString("N")[..8], "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            Table.Clock);
        order.ConfirmPayment(order.GrandTotal, Table.Clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), seller, Table.Clock);
        order.MarkDelivered(Table.Clock);
        await Table.Orders.AddManyAsync([order], Ct);
        return order;
    }

    /// <summary>A buyer opens a dispute the way the public API does: read, open, save with the version read.</summary>
    private async Task<Order> Dispute(string buyer = Buyer, string seller = Seller)
    {
        var stored = await Delivered(buyer, seller);
        var order = (await Table.Orders.FindAsync(stored.Id, Ct))!;
        var version = order.Version;
        order.OpenDispute("Kartu bukan PSA 10.", buyer, Table.Clock);
        await Table.Orders.UpdateAsync(order, version, Ct);
        return order;
    }

    private async Task<int> PointerRows(string userId) =>
        (await Table.Client.QueryAsync(new QueryRequest
        {
            TableName = Table.TableName,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = DynamoTable.Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = new() { S = DynamoTable.UserDisputePk(userId) } },
            ConsistentRead = true,
        })).Count ?? 0;

    [DynamoLocalFact]
    public async Task A_dispute_is_found_from_the_buyer_and_from_the_seller_and_from_nobody_else()
    {
        var order = await Dispute();
        await Dispute("other-buyer", "other-seller");

        (await Table.Orders.ListDisputedForUserAsync(Buyer, Ct)).Select(o => o.Id).ToList().ShouldBe([order.Id]);
        (await Table.Orders.ListDisputedForUserAsync(Seller, Ct)).Select(o => o.Id).ToList().ShouldBe([order.Id]);
        (await Table.Orders.ListDisputedForUserAsync("stranger", Ct)).ShouldBeEmpty();
        (await PointerRows(Buyer)).ShouldBe(1);
        (await PointerRows(Seller)).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task An_order_that_was_never_disputed_is_not_found_this_way()
    {
        await Delivered();

        (await Table.Orders.ListDisputedForUserAsync(Buyer, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task More_disputes_than_one_batch_are_all_returned_newest_order_first()
    {
        var made = new List<Order>();
        for (var i = 0; i < 130; i++)
        {
            Table.Clock.UtcNow = Table.Clock.UtcNow.AddMinutes(1);
            made.Add(await Dispute());
        }

        var found = await Table.Orders.ListDisputedForUserAsync(Buyer, Ct);

        found.Count.ShouldBe(130);
        found.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(130);
        found.Select(o => o.Id).ToList().ShouldBe([.. made.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id, StringComparer.Ordinal).Select(o => o.Id)]);
    }

    [DynamoLocalFact]
    public async Task An_update_that_loses_its_version_check_leaves_no_pointer_behind()
    {
        var stored = await Delivered();
        var order = (await Table.Orders.FindAsync(stored.Id, Ct))!;
        var version = order.Version;
        order.OpenDispute("Kartu bukan PSA 10.", Buyer, Table.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Table.Orders.UpdateAsync(order, version + 7, Ct));

        (await PointerRows(Buyer)).ShouldBe(0);
        (await PointerRows(Seller)).ShouldBe(0);
        (await Table.Orders.FindAsync(stored.Id, Ct))!.Dispute.ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_ruled_dispute_is_still_found_with_its_ruling()
    {
        var order = await Dispute();
        var store = new DynamoDisputeAdminStore(Table.Client, Options);
        var current = (await Table.Orders.FindAsync(order.Id, Ct))!;
        var expected = current.Version;
        current.ResolveDispute(DisputeResolution.RefundToBuyer, Table.Clock);
        var refund = Refund.Record(current, 1_000_000, "Slab bukan PSA 10.", "staff-1", "alice", Table.Clock);
        var decision = DisputeNote.Create(current.Id, "staff-1", "alice", DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Table.Clock);
        var audit = AuditEntry.Create(Table.Clock, "staff-1", "alice", AuditActions.DisputeResolvedForBuyer, AuditTargets.Order, current.Id, "req-1", "Putusan berdasarkan bukti.");
        await store.ResolveAsync(new DisputeResolutionCommit(current, expected, null, refund, decision, [], audit), Ct);

        var found = (await Table.Orders.ListDisputedForUserAsync(Buyer, Ct)).ShouldHaveSingleItem();

        found.Id.ShouldBe(order.Id);
        found.Dispute!.Resolution.ShouldBe(DisputeResolution.RefundToBuyer);
    }

    [DynamoLocalFact]
    public async Task The_backfill_gives_a_dispute_stored_before_the_pointers_existed_its_pointers_once()
    {
        // A dispute as it was stored before this change: the order is disputed, but no pointer rows exist.
        var stored = await Delivered();
        var order = (await Table.Orders.FindAsync(stored.Id, Ct))!;
        order.OpenDispute("Kartu bukan PSA 10.", Buyer, Table.Clock);
        await Table.Client.PutItemAsync(Table.TableName, OrderItem.ToItem(order));
        (await Table.Orders.ListDisputedForUserAsync(Buyer, Ct)).ShouldBeEmpty("invisible until the pointers are written");

        var backfill = (IOrderSweepBackfill)Table.Orders;
        var first = await Drain(backfill);
        var second = await Drain(backfill);

        first.ShouldBeGreaterThanOrEqualTo(2, "at least the two pointers");
        second.ShouldBe(0, "a second pass finds nothing left to write");
        (await Table.Orders.ListDisputedForUserAsync(Buyer, Ct)).ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        (await Table.Orders.ListDisputedForUserAsync(Seller, Ct)).ShouldHaveSingleItem().Id.ShouldBe(order.Id);
    }

    private static async Task<int> Drain(IOrderSweepBackfill backfill)
    {
        var updated = 0;
        string? cursor = null;
        do
        {
            var page = await backfill.BackfillAsync(cursor, 25, CancellationToken.None);
            updated += page.Updated;
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return updated;
    }
}
