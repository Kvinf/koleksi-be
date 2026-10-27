using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// The shape of what the back office sends to DynamoDB, proved without a database: no read is a <c>Scan</c>, the queues are queries on
/// the sparse index's own partitions, and every write is a single transaction that carries its audit rows.
/// </summary>
/// <remarks>
/// What DynamoDB then does with these requests (conditions, concurrency, atomicity) is proved against DynamoDB Local in
/// <c>DisputeAdminStoreTests</c> and <c>HolidayStoreTests</c>, which skip when it is not running.
/// </remarks>
public sealed class DisputeStoreRequestShapeTests
{
    private static readonly DynamoOptions Options = new() { TableName = "koleksi-test", ServiceUrl = "http://localhost:8000" };

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task No_read_the_back_office_makes_is_a_scan()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var disputes = new DynamoDisputeAdminStore(client, Options);
        var holidays = new DynamoHolidayStore(client, Options);

        await disputes.ListOpenAsync(200, CancellationToken.None);
        await disputes.ListResolvedAsync(100, CancellationToken.None);
        await disputes.ListNotesAsync("ORDER1", CancellationToken.None);
        await disputes.AwaitingInfoAsync(["ORDER1", "ORDER2"], CancellationToken.None);
        await disputes.FindRefundAsync("ORDER1", CancellationToken.None);
        await holidays.ListAsync(2026, CancellationToken.None);
        await holidays.FindAsync("2026-03-20", CancellationToken.None);

        recorder.Scans.ShouldBe(0);
        recorder.PointReads.ShouldBe(3, "the waiting-markers batch, the refund, and the one holiday");
    }

    [Fact]
    public async Task Each_queue_asks_every_shard_of_its_own_partition_and_nothing_else()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var disputes = new DynamoDisputeAdminStore(client, Options);

        await disputes.ListOpenAsync(50, CancellationToken.None);
        var open = recorder.Queries.ToList();
        recorder.Queries.Clear();
        await disputes.ListResolvedAsync(50, CancellationToken.None);
        var resolved = recorder.Queries.ToList();

        open.Count.ShouldBe(DynamoTable.AuctionShardCount);
        resolved.Count.ShouldBe(DynamoTable.AuctionShardCount);
        open.ShouldAllBe(q => q.IndexName == DynamoTable.AuctionEndIndex && q.Limit == 50);
        open.Select(q => q.ExpressionAttributeValues[":pk"].S).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(Enumerable.Range(0, DynamoTable.AuctionShardCount).Select(s => DynamoTable.OrderDuePkValue("Disputed", s)).OrderBy(x => x, StringComparer.Ordinal));
        resolved.Select(q => q.ExpressionAttributeValues[":pk"].S).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(Enumerable.Range(0, DynamoTable.AuctionShardCount).Select(s => DynamoTable.OrderDuePkValue("DisputeResolved", s)).OrderBy(x => x, StringComparer.Ordinal));

        // The open queue reads oldest first; the resolved one newest first.
        open.ShouldAllBe(q => q.ScanIndexForward == true);
        resolved.ShouldAllBe(q => q.ScanIndexForward == false);
    }

    [Fact]
    public async Task A_holiday_year_is_one_query_on_one_partition()
    {
        var recorder = DynamoRecorder.Create(out var client);

        await new DynamoHolidayStore(client, Options).ListAsync(2026, CancellationToken.None);

        var query = recorder.Queries.ShouldHaveSingleItem();
        query.IndexName.ShouldBeNull();
        query.ExpressionAttributeValues[":pk"].S.ShouldBe("HOLIDAY#2026");
        query.ExpressionAttributeValues[":year"].S.ShouldBe("2026-");
    }

    [Fact]
    public async Task A_holiday_change_is_one_transaction_with_its_audit_rows()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var store = new DynamoHolidayStore(client, Options);
        var holiday = Holiday.Create("2026-12-31", "Cuti bersama contoh", HolidayKind.CollectiveLeave);
        var audit = AuditEntry.Create(Clock, "staff-1", "alice", AuditActions.HolidayAdded, AuditTargets.Holiday, holiday.Date, "req-1");

        await store.AddAsync(holiday, audit, CancellationToken.None);
        await store.RemoveAsync(holiday.Date, audit, CancellationToken.None);

        recorder.Writes.ShouldBe(2);
        recorder.Transactions.Count.ShouldBe(2);
        recorder.Transactions.ShouldAllBe(t => t.TransactItems.Count == 1 + AuditItems.ItemsPerEntry);
        recorder.Transactions[0].TransactItems[0].Put.ShouldNotBeNull().ConditionExpression.ShouldContain("attribute_not_exists");
        recorder.Transactions[1].TransactItems[0].Delete.ShouldNotBeNull().ConditionExpression.ShouldContain("attribute_exists");
    }

    [Fact]
    public async Task A_ruling_is_one_transaction_conditional_on_the_orders_version_and_state_and_carrying_its_audit_rows()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var store = new DynamoDisputeAdminStore(client, Options);
        var (order, version) = Disputed();
        order.ResolveDispute(DisputeResolution.RefundToBuyer, Clock);
        var refund = Refund.Record(order, 1_000_000, "Slab bukan PSA 10.", "staff-1", "alice", Clock);
        var decision = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Clock);
        var notices = new[]
        {
            OrderNotice.Create(order.BuyerId, OrderNoticeKind.DisputeResolvedForBuyer, order, "Putusan berdasarkan bukti.", Clock),
            OrderNotice.Create(order.SellerId, OrderNoticeKind.DisputeResolvedForBuyer, order, "Putusan berdasarkan bukti.", Clock),
        };
        var audit = AuditEntry.Create(Clock, "staff-1", "alice", AuditActions.DisputeResolvedForBuyer, AuditTargets.Order, order.Id, "req-1", "Putusan berdasarkan bukti.");

        await store.ResolveAsync(new DisputeResolutionCommit(order, version, null, refund, decision, notices, audit), CancellationToken.None);

        recorder.Writes.ShouldBe(1, "one request, so one atomic change");
        var items = recorder.Transactions.ShouldHaveSingleItem().TransactItems;

        // The order, the refund, the decision, the waiting-marker delete, two notices, and the audit entry's rows.
        items.Count.ShouldBe(1 + 1 + 1 + 1 + 2 + AuditItems.ItemsPerEntry);

        var orderWrite = items[0].Put.ShouldNotBeNull();
        orderWrite.ConditionExpression.ShouldBe("#v = :expected AND #state = :disputed");
        orderWrite.ExpressionAttributeValues[":expected"].N.ShouldBe(version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        orderWrite.ExpressionAttributeValues[":disputed"].S.ShouldBe("Disputed");

        // Every new row is written only if it does not exist yet, so a repeat can never overwrite a payout, refund or note.
        items.Where(i => i.Put is not null && !ReferenceEquals(i.Put, orderWrite) && i.Put.Item[DynamoTable.Pk].S.StartsWith("AUDIT#", StringComparison.Ordinal) == false)
            .ShouldAllBe(i => i.Put.ConditionExpression != null && i.Put.ConditionExpression.Contains("attribute_not_exists"));

        items.Count(i => i.Put is not null && i.Put.Item[DynamoTable.Pk].S.StartsWith("AUDIT#", StringComparison.Ordinal)).ShouldBe(AuditItems.ItemsPerEntry);
        items.Count(i => i.Delete is not null).ShouldBe(1);
    }

    [Fact]
    public async Task A_ruling_for_the_seller_writes_the_same_payout_row_a_release_does()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var store = new DynamoDisputeAdminStore(client, Options);
        var (order, version) = Disputed();
        order.ResolveDispute(DisputeResolution.ReleaseToSeller, Clock);
        var payout = Payout.For(order, Clock);
        var decision = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Clock);
        var audit = AuditEntry.Create(Clock, "staff-1", "alice", AuditActions.DisputeResolvedForSeller, AuditTargets.Order, order.Id, "req-1", "Putusan berdasarkan bukti.");

        await store.ResolveAsync(new DisputeResolutionCommit(order, version, payout, null, decision, [], audit), CancellationToken.None);

        var items = recorder.Transactions.ShouldHaveSingleItem().TransactItems;
        var payoutRow = items.Select(i => i.Put).Where(p => p is not null).Single(p => p!.Item[DynamoTable.Pk].S == PayoutItem.ToItem(payout)[DynamoTable.Pk].S);
        payoutRow!.Item.Keys.OrderBy(k => k, StringComparer.Ordinal).ShouldBe(PayoutItem.ToItem(payout).Keys.OrderBy(k => k, StringComparer.Ordinal));
        payoutRow.ConditionExpression.ShouldContain("attribute_not_exists");
    }

    [Fact]
    public async Task A_note_checks_the_order_without_rewriting_it_and_carries_its_audit_rows()
    {
        var recorder = DynamoRecorder.Create(out var client);
        var store = new DynamoDisputeAdminStore(client, Options);
        var (order, _) = Disputed();
        var note = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim video unboxing.", Clock);
        var notice = OrderNotice.Create(order.BuyerId, OrderNoticeKind.InfoRequested, order, note.Text, Clock);
        var audit = AuditEntry.Create(Clock, "staff-1", "alice", AuditActions.DisputeInfoRequested, AuditTargets.Order, order.Id, "req-1", note.Text);

        await store.AddNoteAsync(new DisputeNoteCommit(order, note, true, [notice], audit), CancellationToken.None);

        var items = recorder.Transactions.ShouldHaveSingleItem().TransactItems;
        items[0].ConditionCheck.ShouldNotBeNull().ConditionExpression.ShouldBe("#state = :disputed");
        items.Count(i => i.Put is not null && i.Put.Item[DynamoTable.Pk].S == DynamoTable.OrderPk(order.Id) && i.Put.Item[DynamoTable.Sk].S == DynamoTable.OrderMetaSk())
            .ShouldBe(0, "a note never rewrites the order row");
        items.Count.ShouldBe(1 + 1 + 1 + 1 + AuditItems.ItemsPerEntry);
    }

    private static (Order Order, long Version) Disputed()
    {
        var clock = new FixedClock(Clock.UtcNow.AddDays(-4));
        var order = Order.Create(
            "buyer-sub", "seller-sub",
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), "seller-sub", clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Kartu bukan PSA 10.", "buyer-sub", clock);
        return (order, order.Version);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
