using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The public site's reads of what the back office wrote, against a real table. The rows are written by the back office's own store
/// (the way staff write them), and read back through <see cref="DynamoCaseStore"/>. What a fake cannot vouch for: the staff-internal
/// note is held back by the query itself, a person's notices come from their own partition only, and a notice is delivered once.
/// </summary>
public sealed class CaseStoreTests : DynamoTestBase
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private DynamoOptions Options => new() { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl };

    private DynamoCaseStore Cases => new(Table.Client, Options);

    private DynamoDisputeAdminStore Admin => new(Table.Client, Options);

    private async Task<Order> StoredDispute(string buyer = Buyer, string seller = Seller)
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
        order.OpenDispute("Kartu bukan PSA 10.", buyer, Table.Clock);
        await Table.Orders.AddManyAsync([order], Ct);
        return order;
    }

    private AuditEntry AuditFor(string action, string orderId)
    {
        var entry = AuditEntry.Create(Table.Clock, "staff-1", "alice", action, "order", orderId, "req-" + Guid.NewGuid().ToString("N"), "Alasan yang cukup.", before: "Disputed");
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        return entry;
    }

    /// <summary>Writes a note the way the back office does: one transaction with its audit entry and the notice for whoever is owed one.</summary>
    private async Task Write(Order order, DisputeNoteKind kind, DisputeParty? to, string text)
    {
        var note = DisputeNote.Create(order.Id, "staff-1", "alice", kind, to, text, Table.Clock);
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        var notices = to is { } side
            ? new[] { OrderNotice.Create(side == DisputeParty.Buyer ? order.BuyerId : order.SellerId, OrderNoticeKind.InfoRequested, order, text, Table.Clock) }
            : [];

        await Admin.AddNoteAsync(
            new DisputeNoteCommit(order, note, MarkAwaitingInfo: kind == DisputeNoteKind.InfoRequest, notices, AuditFor("dispute.note_added", order.Id)),
            Ct);
    }

    private async Task Rule(Order order, DisputeResolution resolution, long refund = 1_000_000)
    {
        var stored = (await Table.Orders.FindAsync(order.Id, Ct))!;
        var expected = stored.Version;
        stored.ResolveDispute(resolution, Table.Clock);
        var forSeller = resolution == DisputeResolution.ReleaseToSeller;
        var kind = forSeller ? OrderNoticeKind.DisputeResolvedForSeller : OrderNoticeKind.DisputeResolvedForBuyer;

        await Admin.ResolveAsync(
            new DisputeResolutionCommit(
                stored,
                expected,
                forSeller ? Payout.For(stored, Table.Clock) : null,
                forSeller ? null : Refund.Record(stored, refund, "Slab bukan PSA 10.", "staff-1", "alice", Table.Clock),
                DisputeNote.Create(stored.Id, "staff-1", "alice", DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Table.Clock),
                [
                    OrderNotice.Create(stored.BuyerId, kind, stored, "Putusan berdasarkan bukti.", Table.Clock),
                    OrderNotice.Create(stored.SellerId, kind, stored, "Putusan berdasarkan bukti.", Table.Clock),
                ],
                AuditFor(forSeller ? AuditActions.DisputeResolvedForSeller : AuditActions.DisputeResolvedForBuyer, stored.Id)),
            Ct);
    }

    [DynamoLocalFact]
    public async Task The_internal_note_is_never_returned_and_the_others_come_back_oldest_first()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.Note, null, "RAHASIA staf: pembeli ini sering menyengketakan barang.");
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        await Write(order, DisputeNoteKind.Note, null, "RAHASIA kedua.");
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Mohon bukti pengemasan.");

        var shared = await Cases.ListSharedNotesAsync(order.Id, Ct);

        shared.Select(n => n.Text).ShouldBe(["Mohon kirim foto slab.", "Mohon bukti pengemasan."]);
        shared.ShouldAllBe(n => n.Kind == DisputeNoteKind.InfoRequest);
        shared.Select(n => n.To).ShouldBe([DisputeParty.Buyer, DisputeParty.Seller]);

        // The admin store sees all four: the filter is this store's, and the rows are really there.
        (await Admin.ListNotesAsync(order.Id, Ct)).Count.ShouldBe(4);
    }

    [DynamoLocalFact]
    public async Task Notes_are_read_from_the_asked_for_order_only()
    {
        var mine = await StoredDispute();
        var other = await StoredDispute("other-buyer", "other-seller");
        await Write(mine, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Untuk kasus saya.");
        await Write(other, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Untuk kasus orang lain.");

        (await Cases.ListSharedNotesAsync(mine.Id, Ct)).Select(n => n.Text).ShouldBe(["Untuk kasus saya."]);
    }

    [DynamoLocalFact]
    public async Task A_person_gets_only_their_own_pending_notices_newest_first_and_each_is_delivered_once()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Pertanyaan pertama.");
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Pertanyaan untuk penjual.");
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Pertanyaan kedua.");

        var buyers = await Cases.ListPendingNoticesAsync(Buyer, Ct);
        buyers.Select(n => n.Text).ShouldBe(["Pertanyaan kedua.", "Pertanyaan pertama."]);
        (await Cases.ListPendingNoticesAsync(Seller, Ct)).Select(n => n.Text).ShouldBe(["Pertanyaan untuk penjual."]);
        (await Cases.ListPendingNoticesAsync("someone-else", Ct)).ShouldBeEmpty();

        (await Cases.MarkNoticesDeliveredAsync(Buyer, buyers, Table.Clock.UtcNow, Ct)).Count.ShouldBe(2);

        // Again: no error, and nothing is reported delivered, so a second reader is not told it is new.
        (await Cases.MarkNoticesDeliveredAsync(Buyer, buyers, Table.Clock.UtcNow, Ct)).ShouldBeEmpty();

        (await Cases.ListPendingNoticesAsync(Buyer, Ct)).ShouldBeEmpty();
        (await Cases.ListPendingNoticesAsync(Seller, Ct)).ShouldHaveSingleItem();
    }

    [DynamoLocalFact]
    public async Task Delivering_cannot_touch_a_notice_that_belongs_to_someone_else()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Pertanyaan untuk penjual.");
        var sellers = await Cases.ListPendingNoticesAsync(Seller, Ct);

        // The buyer asks to mark the seller's notice: it is skipped (and a forged copy under the buyer's id has no such row).
        (await Cases.MarkNoticesDeliveredAsync(Buyer, sellers, Table.Clock.UtcNow, Ct)).ShouldBeEmpty();
        (await Cases.MarkNoticesDeliveredAsync(Buyer, [sellers[0] with { UserId = Buyer }], Table.Clock.UtcNow, Ct)).ShouldBeEmpty();

        (await Cases.ListPendingNoticesAsync(Seller, Ct)).ShouldHaveSingleItem();
    }

    [DynamoLocalFact]
    public async Task Refunds_are_found_by_order_in_one_batch_and_an_order_without_one_is_absent()
    {
        var refunded = await StoredDispute();
        var released = await StoredDispute("buyer-2", "seller-2");
        var undecided = await StoredDispute("buyer-3", "seller-3");
        await Rule(refunded, DisputeResolution.RefundToBuyer, 1_500_000);
        await Rule(released, DisputeResolution.ReleaseToSeller);

        var found = await Cases.FindRefundsAsync([refunded.Id, released.Id, undecided.Id, refunded.Id], Ct);

        found.Keys.ShouldBe([refunded.Id]);
        found[refunded.Id].Amount.Rupiah.ShouldBe(1_500_000);
        found[refunded.Id].BuyerId.ShouldBe(Buyer);
        found[refunded.Id].Status.ShouldBe(RefundStatus.Recorded);
        (await Cases.FindRefundsAsync([], Ct)).ShouldBeEmpty();
    }

    private async Task<DateTimeOffset> LatestRequestAt(Order order) =>
        (await Cases.ListSharedNotesAsync(order.Id, Ct)).Where(n => n.Kind == DisputeNoteKind.InfoRequest).Max(n => n.At);

    private DisputeNote ReplyFrom(Order order, string authorId, DisputeParty side, string text = "Ini bukti yang diminta, lihat foto terlampir.")
    {
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        return DisputeNote.CreateReply(order.Id, authorId, side, text, Table.Clock);
    }

    [DynamoLocalFact]
    public async Task A_reply_is_stored_read_back_in_order_and_clears_the_staff_marker_for_the_request_it_answered()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        (await Admin.AwaitingInfoAsync([order.Id], Ct)).ShouldContain(order.Id);

        var asked = await LatestRequestAt(order);
        await Cases.AddReplyAsync(new CaseReplyCommit(order, ReplyFrom(order, Buyer, DisputeParty.Buyer), asked), Ct);

        var shared = await Cases.ListSharedNotesAsync(order.Id, Ct);
        shared.Select(n => n.Kind).ShouldBe([DisputeNoteKind.InfoRequest, DisputeNoteKind.Reply]);
        shared[1].To.ShouldBe(DisputeParty.Buyer);
        shared[1].StaffId.ShouldBe(Buyer);
        (await Admin.AwaitingInfoAsync([order.Id], Ct)).ShouldNotContain(order.Id);

        // Staff see it in the case file, beside their own notes.
        (await Admin.ListNotesAsync(order.Id, Ct)).Select(n => n.Kind).ShouldContain(DisputeNoteKind.Reply);
    }

    [DynamoLocalFact]
    public async Task A_reply_that_leaves_the_other_side_still_asked_keeps_the_marker()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Mohon bukti pengemasan.");

        await Cases.AddReplyAsync(new CaseReplyCommit(order, ReplyFrom(order, Buyer, DisputeParty.Buyer), ClearAwaitingFor: null), Ct);

        (await Admin.AwaitingInfoAsync([order.Id], Ct)).ShouldContain(order.Id);
    }

    [DynamoLocalFact]
    public async Task A_reply_loses_to_a_request_staff_made_meanwhile_and_writes_nothing()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        var staleAsked = await LatestRequestAt(order);

        // Staff ask again after the reply was prepared: the marker now carries the newer time.
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon juga foto label.");

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Cases.AddReplyAsync(new CaseReplyCommit(order, ReplyFrom(order, Buyer, DisputeParty.Buyer), staleAsked), Ct));

        (await Cases.ListSharedNotesAsync(order.Id, Ct)).ShouldNotContain(n => n.Kind == DisputeNoteKind.Reply);
        (await Admin.AwaitingInfoAsync([order.Id], Ct)).ShouldContain(order.Id);
    }

    [DynamoLocalFact]
    public async Task A_reply_to_a_case_that_was_ruled_on_is_refused_and_writes_nothing()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        var asked = await LatestRequestAt(order);
        await Rule(order, DisputeResolution.ReleaseToSeller);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => Cases.AddReplyAsync(new CaseReplyCommit(order, ReplyFrom(order, Buyer, DisputeParty.Buyer), asked), Ct));

        (await Cases.ListSharedNotesAsync(order.Id, Ct)).ShouldNotContain(n => n.Kind == DisputeNoteKind.Reply);
    }

    [DynamoLocalFact]
    public async Task A_ruling_leaves_its_decision_visible_and_the_open_request_gone_from_the_marker_only()
    {
        var order = await StoredDispute();
        await Write(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto slab.");
        await Rule(order, DisputeResolution.RefundToBuyer);

        var shared = await Cases.ListSharedNotesAsync(order.Id, Ct);

        shared.Select(n => n.Kind).ShouldBe([DisputeNoteKind.InfoRequest, DisputeNoteKind.Decision]);
        (await Cases.ListPendingNoticesAsync(Buyer, Ct)).Select(n => n.Kind)
            .ShouldBe([OrderNoticeKind.DisputeResolvedForBuyer, OrderNoticeKind.InfoRequested]);
    }
}
