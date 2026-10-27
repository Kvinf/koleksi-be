using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// How the dispute queues are keyed on the order itself, and how the case file's rows are stored. The queues are queries on a sparse index
/// the order carries only while it is in the state, so these tests are what say the key appears and disappears at the right moments.
/// </summary>
public sealed class DisputeQueueKeyTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private static readonly DateTimeOffset Start = new(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);

    private static Order Disputed(FixedClock clock)
    {
        var order = Order.Create(
            Buyer, Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), Seller, clock);
        order.MarkDelivered(clock);
        clock.Advance(TimeSpan.FromHours(5));
        order.OpenDispute("Kartu bukan PSA 10.", Buyer, clock);
        return order;
    }

    // ─── The queue keys ──────────────────────────────────────────────────────────

    [Fact]
    public void An_open_dispute_is_keyed_by_when_it_was_opened_in_a_partition_no_sweep_asks_for()
    {
        var clock = new FixedClock(Start);
        var order = Disputed(clock);

        var item = OrderItem.ToItem(order);

        item[DynamoTable.AuctionEndPk].S.ShouldBe(DynamoTable.OrderDuePkValue("Disputed", DynamoTable.ShardOf(order.Id)));
        item[DynamoTable.AuctionEndSk].S.ShouldBe(DynamoTable.OrderDueSkValue(order.Dispute!.OpenedAt, order.Id));
    }

    [Fact]
    public void A_dispute_leaves_the_open_queue_and_joins_the_resolved_one_when_it_is_ruled_on()
    {
        foreach (var resolution in new[] { DisputeResolution.ReleaseToSeller, DisputeResolution.RefundToBuyer })
        {
            var clock = new FixedClock(Start);
            var order = Disputed(clock);
            var opened = OrderItem.ToItem(order)[DynamoTable.AuctionEndPk].S;

            clock.Advance(TimeSpan.FromDays(2));
            order.ResolveDispute(resolution, clock);
            var item = OrderItem.ToItem(order);

            opened.ShouldContain("#Disputed#");
            item[DynamoTable.AuctionEndPk].S.ShouldBe(DynamoTable.OrderDuePkValue("DisputeResolved", DynamoTable.ShardOf(order.Id)), resolution.ToString());
            item[DynamoTable.AuctionEndSk].S.ShouldBe(DynamoTable.OrderDueSkValue(order.Dispute!.ResolvedAt!.Value, order.Id), resolution.ToString());
        }
    }

    [Fact]
    public void An_order_released_without_a_dispute_stays_out_of_both_queues()
    {
        var clock = new FixedClock(Start);
        var order = Order.Create(
            Buyer, Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), Seller, clock);
        order.MarkDelivered(clock);
        order.Release(confirmedByBuyer: true, clock);

        OrderItem.ToItem(order).ContainsKey(DynamoTable.AuctionEndPk).ShouldBeFalse();
    }

    [Fact]
    public void The_dispute_queues_never_share_a_partition_with_each_other_or_with_a_sweep()
    {
        var partitions = new List<string>();
        for (var shard = 0; shard < DynamoTable.AuctionShardCount; shard++)
        {
            foreach (var state in new[] { "Disputed", "DisputeResolved", "AwaitingPayment", "Delivered", OrderItem.TrackingState })
            {
                partitions.Add(DynamoTable.OrderDuePkValue(state, shard));
            }
        }

        partitions.Distinct(StringComparer.Ordinal).Count().ShouldBe(partitions.Count);
        DynamoTable.DisputedQueueState.ShouldBe("Disputed");
        DynamoTable.DisputeResolvedQueueState.ShouldBe("DisputeResolved");
    }

    [Fact]
    public void The_open_queue_reads_oldest_first()
    {
        var older = DynamoTable.OrderDueSkValue(Start, "B");
        var newer = DynamoTable.OrderDueSkValue(Start.AddDays(3), "A");

        string.CompareOrdinal(older, newer).ShouldBeLessThan(0);
    }

    [Fact]
    public void An_order_with_a_dispute_reads_back_whole_from_its_item()
    {
        var clock = new FixedClock(Start);
        var order = Disputed(clock);
        clock.Advance(TimeSpan.FromDays(1));
        order.ResolveDispute(DisputeResolution.RefundToBuyer, clock);

        var read = OrderItem.ToDomain(OrderItem.ToItem(order));

        read.EscrowState.ShouldBe(EscrowState.Refunded);
        read.Dispute.ShouldBe(order.Dispute);
        read.Version.ShouldBe(order.Version);
        OrderItem.DueEntryFor(read).ShouldBe(OrderItem.DueEntryFor(order));
    }

    // ─── The case file ───────────────────────────────────────────────────────────

    [Fact]
    public void A_case_note_round_trips_and_lives_in_the_orders_partition()
    {
        var clock = new FixedClock(Start);
        var note = DisputeNote.Create("ORDER1", "staff-1", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Mohon kirim video unboxing.", clock);

        var item = DisputeItems.NoteToItem(note);

        item[DynamoTable.Pk].S.ShouldBe(DynamoTable.OrderPk("ORDER1"));
        item[DynamoTable.Sk].S.ShouldBe(DynamoTable.DisputeNoteSk(note.Id));
        DisputeItems.NoteToDomain(item).ShouldBe(note);
    }

    [Fact]
    public void An_internal_note_has_no_addressee_in_storage()
    {
        var clock = new FixedClock(Start);
        var note = DisputeNote.Create("ORDER1", "staff-1", "alice", DisputeNoteKind.Note, null, "Catatan internal.", clock);

        var item = DisputeItems.NoteToItem(note);

        item.ContainsKey("to").ShouldBeFalse();
        DisputeItems.NoteToDomain(item).To.ShouldBeNull();
    }

    [Fact]
    public void Notes_sort_in_the_order_they_were_written()
    {
        var clock = new FixedClock(Start);
        var first = DisputeNote.Create("ORDER1", "s", "alice", DisputeNoteKind.Note, null, "Catatan pertama.", clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = DisputeNote.Create("ORDER1", "s", "alice", DisputeNoteKind.Note, null, "Catatan kedua.", clock);

        string.CompareOrdinal(DynamoTable.DisputeNoteSk(first.Id), DynamoTable.DisputeNoteSk(second.Id)).ShouldBeLessThan(0);
        DynamoTable.DisputeNoteSk(first.Id).ShouldStartWith(DynamoTable.DisputeNoteSkPrefix());
    }

    [Fact]
    public void A_refund_round_trips_with_everything_staff_and_the_audit_need()
    {
        var clock = new FixedClock(Start);
        var order = Disputed(clock);
        order.ResolveDispute(DisputeResolution.RefundToBuyer, clock);
        var refund = Refund.Record(order, 1_500_000, "Slab bukan PSA 10.", "staff-1", "alice", clock);

        var item = DisputeItems.RefundToItem(refund);
        var read = DisputeItems.RefundToDomain(item);

        item[DynamoTable.Pk].S.ShouldBe(DynamoTable.RefundPk(order.Id));
        item[DynamoTable.Sk].S.ShouldBe(DynamoTable.RefundSk());
        read.Amount.Rupiah.ShouldBe(1_500_000);
        read.Reason.ShouldBe("Slab bukan PSA 10.");
        read.StaffId.ShouldBe("staff-1");
        read.StaffName.ShouldBe("alice");
        read.BuyerId.ShouldBe(Buyer);
        read.OrderNumber.ShouldBe(order.OrderNumber);
        read.Status.ShouldBe(RefundStatus.Recorded);
        read.CreatedAt.ShouldBe(refund.CreatedAt);
    }

    [Fact]
    public void A_notice_is_stored_pending_because_nothing_delivers_it_yet()
    {
        var clock = new FixedClock(Start);
        var order = Disputed(clock);
        var notice = OrderNotice.Create(Buyer, OrderNoticeKind.InfoRequested, order, "Mohon kirim video unboxing.", clock);

        var item = DisputeItems.NoticeToItem(notice);

        item[DynamoTable.Pk].S.ShouldBe(DynamoTable.NoticePk(Buyer));
        item["status"].S.ShouldBe("pending");
        item["orderNumber"].S.ShouldBe(order.OrderNumber);
    }

    [Fact]
    public void The_information_marker_sits_in_the_orders_partition_under_one_fixed_key()
    {
        var clock = new FixedClock(Start);
        var note = DisputeNote.Create("ORDER1", "s", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon foto lengkap.", clock);

        var marker = DisputeItems.InfoMarker(note);

        marker[DynamoTable.Pk].S.ShouldBe(DynamoTable.OrderPk("ORDER1"));
        marker[DynamoTable.Sk].S.ShouldBe(DynamoTable.DisputeInfoSk());
    }

    // ─── Holidays ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_years_holidays_share_one_partition_and_sort_by_date()
    {
        DynamoTable.HolidayPk(2026).ShouldBe("HOLIDAY#2026");
        string.CompareOrdinal(DynamoTable.HolidaySk("2026-03-20"), DynamoTable.HolidaySk("2026-12-25")).ShouldBeLessThan(0);

        // The seed marker sorts before every date and does not begin with a year, so a year-prefixed query never returns it.
        DynamoTable.HolidaySeededSk().ShouldNotStartWith("2026-");
    }

    [Fact]
    public void The_seed_fits_in_one_transaction_with_its_marker_and_audit_rows()
    {
        // 1 marker + the holidays + the audit entry's rows must stay within DynamoDB's 100 items per transaction.
        (HolidaySeed.Year2026.Count + 1 + AuditItems.ItemsPerEntry).ShouldBeLessThanOrEqualTo(100);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = now;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
