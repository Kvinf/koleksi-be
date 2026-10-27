using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The back office's dispute store against a real table. What a fake cannot vouch for, and what these tests are for: DynamoDB applies a
/// ruling and its audit rows together or not at all, two rulings at once give exactly one winner, and the queues come from a sparse
/// index that the order itself feeds.
/// </summary>
public sealed class DisputeAdminStoreTests : DynamoTestBase
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";

    private DynamoOptions Options => new() { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl };

    private DynamoDisputeAdminStore Store => new(Table.Client, Options);

    private DynamoAuditLog Audit => new(Table.Client, Options, Table.Clock);

    private DynamoPayoutRepository Payouts => new(Table.Client, Options);

    /// <summary>A delivered order whose buyer opened a dispute <paramref name="openedAgo"/> before the table clock, stored with its queue key.</summary>
    private async Task<Order> StoredDispute(TimeSpan? openedAgo = null, string buyer = Buyer, string seller = Seller)
    {
        var clock = new FixedClock { UtcNow = Table.Clock.UtcNow - (openedAgo ?? TimeSpan.Zero) };
        var order = Order.Create(
            buyer, seller,
            [new OrderLine("L-" + Guid.NewGuid().ToString("N")[..8], "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 3, 1)), seller, clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Kartu bukan PSA 10.", buyer, clock);
        await Table.Orders.AddManyAsync([order], Ct);
        return order;
    }

    private AuditEntry AuditFor(string action, string orderId, string staff = "staff-1", string name = "alice", string reason = "Alasan yang cukup.")
    {
        var entry = AuditEntry.Create(Table.Clock, staff, name, action, "order", orderId, "req-" + Guid.NewGuid().ToString("N"), reason, before: "Disputed");
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        return entry;
    }

    private async Task<DisputeResolutionCommit> RulingFor(
        string orderId, DisputeResolution resolution, string staff = "staff-1", string name = "alice", long refund = 1_000_000)
    {
        var order = (await Table.Orders.FindAsync(orderId, Ct))!;
        var expected = order.Version;
        order.ResolveDispute(resolution, Table.Clock);

        var forSeller = resolution == DisputeResolution.ReleaseToSeller;
        var payout = forSeller ? Payout.For(order, Table.Clock) : null;
        var refundRecord = forSeller ? null : Refund.Record(order, refund, "Slab bukan PSA 10.", staff, name, Table.Clock);
        var decision = DisputeNote.Create(order.Id, staff, name, DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Table.Clock);
        var kind = forSeller ? OrderNoticeKind.DisputeResolvedForSeller : OrderNoticeKind.DisputeResolvedForBuyer;

        return new DisputeResolutionCommit(
            order, expected, payout, refundRecord, decision,
            [
                OrderNotice.Create(order.BuyerId, kind, order, "Putusan berdasarkan bukti.", Table.Clock),
                OrderNotice.Create(order.SellerId, kind, order, "Putusan berdasarkan bukti.", Table.Clock),
            ],
            AuditFor(forSeller ? AuditActions.DisputeResolvedForSeller : AuditActions.DisputeResolvedForBuyer, order.Id, staff, name));
    }

    private async Task<int> RowsWithPrefix(string pk, string? skPrefix = null)
    {
        var response = await Table.Client.QueryAsync(new QueryRequest
        {
            TableName = Table.TableName,
            KeyConditionExpression = skPrefix is null ? "#pk = :pk" : "#pk = :pk AND begins_with(#sk, :sk)",
            ExpressionAttributeNames = skPrefix is null
                ? new Dictionary<string, string> { ["#pk"] = DynamoTable.Pk }
                : new Dictionary<string, string> { ["#pk"] = DynamoTable.Pk, ["#sk"] = DynamoTable.Sk },
            ExpressionAttributeValues = skPrefix is null
                ? new Dictionary<string, AttributeValue> { [":pk"] = new() { S = pk } }
                : new Dictionary<string, AttributeValue> { [":pk"] = new() { S = pk }, [":sk"] = new() { S = skPrefix } },
            ConsistentRead = true,
        });
        return response.Count ?? 0;
    }

    private async Task<IReadOnlyList<AuditEntry>> AuditOf(string orderId) =>
        (await Audit.ListAsync(new AuditQuery(null, "order", orderId, null, 50), Ct)).Items;

    // ─── The queues ──────────────────────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task The_open_queue_is_oldest_first_across_all_shards_and_holds_only_open_disputes()
    {
        var opened = new List<Order>();
        foreach (var hours in new[] { 5, 90, 30, 200, 12, 48, 120, 7, 61, 3 })
        {
            opened.Add(await StoredDispute(TimeSpan.FromHours(hours)));
        }

        // Neither of these belongs in the queue: one is resolved, one was never disputed.
        var ruled = await StoredDispute(TimeSpan.FromHours(400));
        await Store.ResolveAsync(await RulingFor(ruled.Id, DisputeResolution.ReleaseToSeller), Ct);

        var queue = await Store.ListOpenAsync(100, Ct);

        queue.Select(o => o.Id).ShouldBe([.. opened.OrderBy(o => o.Dispute!.OpenedAt).Select(o => o.Id)]);
        (await Store.ListOpenAsync(3, Ct)).Select(o => o.Id).ShouldBe([.. opened.OrderBy(o => o.Dispute!.OpenedAt).Take(3).Select(o => o.Id)]);
    }

    [DynamoLocalFact]
    public async Task A_ruled_dispute_moves_from_the_open_queue_to_the_resolved_one_newest_first()
    {
        var first = await StoredDispute(TimeSpan.FromHours(10));
        var second = await StoredDispute(TimeSpan.FromHours(9));

        await Store.ResolveAsync(await RulingFor(first.Id, DisputeResolution.ReleaseToSeller), Ct);
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddHours(1);
        await Store.ResolveAsync(await RulingFor(second.Id, DisputeResolution.RefundToBuyer), Ct);

        (await Store.ListOpenAsync(10, Ct)).ShouldBeEmpty();
        var resolved = await Store.ListResolvedAsync(10, Ct);
        resolved.Select(o => o.Id).ShouldBe([second.Id, first.Id]);
        resolved[0].Dispute!.Resolution.ShouldBe(DisputeResolution.RefundToBuyer);
        resolved[1].Dispute!.Resolution.ShouldBe(DisputeResolution.ReleaseToSeller);
    }

    // ─── A ruling is one transaction ─────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task A_ruling_for_the_seller_writes_the_order_the_payout_the_decision_both_notices_and_the_audit_together()
    {
        var order = await StoredDispute();
        var commit = await RulingFor(order.Id, DisputeResolution.ReleaseToSeller);

        await Store.ResolveAsync(commit, Ct);

        (await Table.Orders.FindAsync(order.Id, Ct))!.EscrowState.ShouldBe(EscrowState.Released);
        var payout = (await Payouts.FindByOrderAsync(order.Id, Ct)).ShouldNotBeNull();
        payout.Amount.ShouldBe(order.SellerPayout);
        (await Store.FindRefundAsync(order.Id, Ct)).ShouldBeNull();
        (await Store.ListNotesAsync(order.Id, Ct)).ShouldHaveSingleItem().Kind.ShouldBe(DisputeNoteKind.Decision);
        (await RowsWithPrefix(DynamoTable.NoticePk(Buyer))).ShouldBe(1);
        (await RowsWithPrefix(DynamoTable.NoticePk(Seller))).ShouldBe(1);
        (await AuditOf(order.Id)).ShouldHaveSingleItem().Action.ShouldBe("dispute.resolved_for_seller");
    }

    [DynamoLocalFact]
    public async Task A_ruling_for_the_buyer_stores_the_refund_with_who_decided_and_no_payout()
    {
        var order = await StoredDispute();

        await Store.ResolveAsync(await RulingFor(order.Id, DisputeResolution.RefundToBuyer, "staff-7", "bob", refund: 1_500_000), Ct);

        var refund = (await Store.FindRefundAsync(order.Id, Ct)).ShouldNotBeNull();
        refund.Amount.Rupiah.ShouldBe(1_500_000);
        refund.StaffId.ShouldBe("staff-7");
        refund.StaffName.ShouldBe("bob");
        refund.Status.ShouldBe(RefundStatus.Recorded);
        (await Payouts.FindByOrderAsync(order.Id, Ct)).ShouldBeNull();
        (await Table.Orders.FindAsync(order.Id, Ct))!.EscrowState.ShouldBe(EscrowState.Refunded);
        (await AuditOf(order.Id)).ShouldHaveSingleItem().ActorName.ShouldBe("bob");
    }

    // ─── The remainder of a partial refund ───────────────────────────────────────

    private async Task<Order> RefundedWith(RemainderChoice? choice, long refund = 1_000_000)
    {
        var order = await StoredDispute();
        var current = (await Table.Orders.FindAsync(order.Id, Ct))!;
        var expected = current.Version;
        current.ResolveDispute(DisputeResolution.RefundToBuyer, Table.Clock);
        var record = Refund.Record(current, refund, "Slab bukan PSA 10.", "staff-1", "alice", Table.Clock, choice);
        var payout = record.RemainderPayout(current, Table.Clock);
        var decision = DisputeNote.Create(current.Id, "staff-1", "alice", DisputeNoteKind.Decision, null, "Putusan berdasarkan bukti.", Table.Clock);

        await Store.ResolveAsync(
            new DisputeResolutionCommit(
                current, expected, payout, record, decision,
                [OrderNotice.Create(current.BuyerId, OrderNoticeKind.DisputeResolvedForBuyer, current, "Putusan.", Table.Clock)],
                AuditFor(AuditActions.DisputeResolvedForBuyer, current.Id)),
            Ct);
        return current;
    }

    private async Task<RemainderSettlementCommit> SettlementFor(string orderId, RemainderChoice choice)
    {
        var order = (await Table.Orders.FindAsync(orderId, Ct))!;
        var refund = (await Store.FindRefundAsync(orderId, Ct))!;
        refund.SettleRemainder(order, choice, Table.Clock);
        var payout = refund.RemainderPayout(order, Table.Clock)!;
        return new RemainderSettlementCommit(order, refund, payout, AuditFor(AuditActions.DisputeRemainderSettled, orderId));
    }

    [DynamoLocalFact]
    public async Task A_refund_stores_what_became_of_the_remainder_and_reads_it_back()
    {
        var held = await RefundedWith(choice: null);
        var released = await RefundedWith(new RemainderChoice(RemainderMode.Release));
        var split = await RefundedWith(new RemainderChoice(RemainderMode.Split, 700_000));
        var full = await RefundedWith(choice: null, refund: 4_274_000);

        var heldRefund = (await Store.FindRefundAsync(held.Id, Ct))!;
        heldRefund.Remainder.Rupiah.ShouldBe(3_274_000);
        heldRefund.Disposition.ShouldBe(RemainderDisposition.Held);
        heldRefund.SellerPayout.ShouldBe(Money.Zero);
        heldRefund.SettledAt.ShouldBeNull();
        (await Payouts.FindByOrderAsync(held.Id, Ct)).ShouldBeNull();

        var releasedRefund = (await Store.FindRefundAsync(released.Id, Ct))!;
        releasedRefund.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        releasedRefund.SellerPayout.Rupiah.ShouldBe(3_110_300);
        (await Payouts.FindByOrderAsync(released.Id, Ct))!.Amount.Rupiah.ShouldBe(3_110_300);

        var splitRefund = (await Store.FindRefundAsync(split.Id, Ct))!;
        splitRefund.Disposition.ShouldBe(RemainderDisposition.Split);
        (await Payouts.FindByOrderAsync(split.Id, Ct))!.Amount.Rupiah.ShouldBe(700_000);

        var fullRefund = (await Store.FindRefundAsync(full.Id, Ct))!;
        fullRefund.Remainder.ShouldBe(Money.Zero);
        fullRefund.Disposition.ShouldBe(RemainderDisposition.None);
        (await Payouts.FindByOrderAsync(full.Id, Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task A_refund_row_written_before_remainders_existed_reads_as_having_none()
    {
        var order = await RefundedWith(choice: null, refund: 4_274_000);
        var key = new Dictionary<string, AttributeValue>
        {
            [DynamoTable.Pk] = new() { S = DynamoTable.RefundPk(order.Id) },
            [DynamoTable.Sk] = new() { S = DynamoTable.RefundSk() },
        };

        // Strip the new fields from the stored row, the way a row from before this change looks.
        await Table.Client.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = Table.TableName,
            Key = key,
            UpdateExpression = "REMOVE #r, #d, #s",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#r"] = "remainder", ["#d"] = "disposition", ["#s"] = "sellerPayout" },
        });

        var old = (await Store.FindRefundAsync(order.Id, Ct)).ShouldNotBeNull();
        old.Amount.Rupiah.ShouldBe(4_274_000);
        old.Remainder.ShouldBe(Money.Zero);
        old.Disposition.ShouldBe(RemainderDisposition.None);
        old.SellerPayout.ShouldBe(Money.Zero);
    }

    [DynamoLocalFact]
    public async Task Settling_a_held_remainder_moves_the_refund_writes_the_payout_and_audits_in_one_transaction()
    {
        var order = await RefundedWith(choice: null);
        var commit = await SettlementFor(order.Id, new RemainderChoice(RemainderMode.Release));

        await Store.SettleRemainderAsync(commit, Ct);

        var refund = (await Store.FindRefundAsync(order.Id, Ct))!;
        refund.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        refund.SellerPayout.Rupiah.ShouldBe(3_110_300);
        refund.SettledAt.ShouldNotBeNull();
        var payout = (await Payouts.FindByOrderAsync(order.Id, Ct)).ShouldNotBeNull();
        payout.Amount.Rupiah.ShouldBe(3_110_300);
        payout.SellerId.ShouldBe(Seller);
        (await AuditOf(order.Id)).Select(e => e.Action).ShouldBe(["dispute.resolved_for_buyer", "dispute.remainder_settled"], ignoreOrder: true);
    }

    [DynamoLocalFact]
    public async Task A_remainder_can_only_be_settled_once_and_the_loser_leaves_no_payout_or_audit_row()
    {
        var order = await RefundedWith(choice: null);
        var first = await SettlementFor(order.Id, new RemainderChoice(RemainderMode.Split, 500_000));
        var second = await SettlementFor(order.Id, new RemainderChoice(RemainderMode.Release));
        await Store.SettleRemainderAsync(first, Ct);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Store.SettleRemainderAsync(second, Ct));

        (await Store.FindRefundAsync(order.Id, Ct))!.Disposition.ShouldBe(RemainderDisposition.Split);
        (await Payouts.FindByOrderAsync(order.Id, Ct))!.Amount.Rupiah.ShouldBe(500_000);
        (await AuditOf(order.Id)).Count(e => e.Action == "dispute.remainder_settled").ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Two_staff_settling_at_once_give_exactly_one_winner_and_one_payout()
    {
        for (var round = 0; round < 6; round++)
        {
            var order = await RefundedWith(choice: null);
            var release = await SettlementFor(order.Id, new RemainderChoice(RemainderMode.Release));
            var split = await SettlementFor(order.Id, new RemainderChoice(RemainderMode.Split, 400_000));

            var outcomes = await Task.WhenAll(Attempt(release), Attempt(split));

            outcomes.Count(won => won).ShouldBe(1, $"round {round}: exactly one settlement lands");
            var refund = (await Store.FindRefundAsync(order.Id, Ct))!;
            var payout = (await Payouts.FindByOrderAsync(order.Id, Ct)).ShouldNotBeNull();
            payout.Amount.ShouldBe(refund.SellerPayout, "the payout is the winner's, whichever won");
            (await AuditOf(order.Id)).Count(e => e.Action == "dispute.remainder_settled").ShouldBe(1);
        }

        async Task<bool> Attempt(RemainderSettlementCommit commit)
        {
            try
            {
                await Store.SettleRemainderAsync(commit, Ct);
                return true;
            }
            catch (ConcurrencyConflictException)
            {
                return false;
            }
        }
    }

    [DynamoLocalFact]
    public async Task Settling_a_refund_that_was_never_held_is_refused_and_writes_nothing()
    {
        var order = await RefundedWith(new RemainderChoice(RemainderMode.Release));
        var refund = (await Store.FindRefundAsync(order.Id, Ct))!;
        var current = (await Table.Orders.FindAsync(order.Id, Ct))!;

        // A caller that built its commit from a stale read still cannot settle: the stored refund is what decides.
        var forged = Refund.Record(current, 1_000_000, "Slab bukan PSA 10.", "staff-1", "alice", Table.Clock);
        forged.SettleRemainder(current, new RemainderChoice(RemainderMode.Split, 100_000), Table.Clock);
        var commit = new RemainderSettlementCommit(current, forged, forged.RemainderPayout(current, Table.Clock)!, AuditFor(AuditActions.DisputeRemainderSettled, order.Id));

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Store.SettleRemainderAsync(commit, Ct));

        (await Store.FindRefundAsync(order.Id, Ct))!.SellerPayout.ShouldBe(refund.SellerPayout);
        (await Payouts.FindByOrderAsync(order.Id, Ct))!.Amount.Rupiah.ShouldBe(3_110_300);
        (await AuditOf(order.Id)).Count(e => e.Action == "dispute.remainder_settled").ShouldBe(0);
    }

    [DynamoLocalFact]
    public async Task A_ruling_that_loses_its_condition_leaves_no_trace_not_even_an_audit_row()
    {
        var order = await StoredDispute();
        var stale = await RulingFor(order.Id, DisputeResolution.ReleaseToSeller);
        var staleCommit = stale with { ExpectedVersion = stale.ExpectedVersion + 5 };

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Store.ResolveAsync(staleCommit, Ct));

        var unchanged = (await Table.Orders.FindAsync(order.Id, Ct))!;
        unchanged.EscrowState.ShouldBe(EscrowState.Disputed);
        unchanged.Version.ShouldBe(stale.ExpectedVersion);
        (await Payouts.FindByOrderAsync(order.Id, Ct)).ShouldBeNull();
        (await Store.ListNotesAsync(order.Id, Ct)).ShouldBeEmpty();
        (await RowsWithPrefix(DynamoTable.NoticePk(Buyer))).ShouldBe(0);
        (await AuditOf(order.Id)).ShouldBeEmpty();
        (await Store.ListOpenAsync(10, Ct)).ShouldHaveSingleItem().Id.ShouldBe(order.Id);
    }

    [DynamoLocalFact]
    public async Task Ruling_on_an_order_that_is_already_ruled_on_is_refused_and_audits_nothing_more()
    {
        var order = await StoredDispute();
        var first = await RulingFor(order.Id, DisputeResolution.ReleaseToSeller);
        var second = await RulingFor(order.Id, DisputeResolution.RefundToBuyer, "staff-2", "bob");

        await Store.ResolveAsync(first, Ct);
        await Should.ThrowAsync<ConcurrencyConflictException>(() => Store.ResolveAsync(second, Ct));

        (await Table.Orders.FindAsync(order.Id, Ct))!.EscrowState.ShouldBe(EscrowState.Released);
        (await Store.FindRefundAsync(order.Id, Ct)).ShouldBeNull();
        (await AuditOf(order.Id)).ShouldHaveSingleItem().ActorName.ShouldBe("alice");
    }

    [DynamoLocalFact]
    public async Task Two_staff_ruling_at_once_give_exactly_one_winner()
    {
        for (var round = 0; round < 6; round++)
        {
            var order = await StoredDispute();

            // Both read the dispute as open, then both try to commit, for opposite sides.
            var forSeller = await RulingFor(order.Id, DisputeResolution.ReleaseToSeller, "staff-1", "alice");
            var forBuyer = await RulingFor(order.Id, DisputeResolution.RefundToBuyer, "staff-2", "bob");

            var outcomes = await Task.WhenAll(Attempt(forSeller), Attempt(forBuyer));

            outcomes.Count(won => won).ShouldBe(1, $"round {round}: exactly one ruling lands");
            var audit = (await AuditOf(order.Id)).ShouldHaveSingleItem();
            var stored = (await Table.Orders.FindAsync(order.Id, Ct))!;

            // Whoever won, the rows agree with each other: one ruling's payout or refund, never both, and the audit names the winner.
            var payout = await Payouts.FindByOrderAsync(order.Id, Ct);
            var refund = await Store.FindRefundAsync(order.Id, Ct);
            if (stored.EscrowState == EscrowState.Released)
            {
                payout.ShouldNotBeNull();
                refund.ShouldBeNull();
                audit.ActorName.ShouldBe("alice");
            }
            else
            {
                stored.EscrowState.ShouldBe(EscrowState.Refunded);
                payout.ShouldBeNull();
                refund.ShouldNotBeNull();
                audit.ActorName.ShouldBe("bob");
            }

            (await Store.ListNotesAsync(order.Id, Ct)).ShouldHaveSingleItem();
        }

        async Task<bool> Attempt(DisputeResolutionCommit commit)
        {
            try
            {
                await Store.ResolveAsync(commit, Ct);
                return true;
            }
            catch (ConcurrencyConflictException)
            {
                return false;
            }
        }
    }

    // ─── Notes ───────────────────────────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task An_information_request_stores_the_note_the_waiting_marker_the_notice_and_the_audit_together()
    {
        var order = await StoredDispute();
        var note = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim video unboxing.", Table.Clock);
        var notice = OrderNotice.Create(Buyer, OrderNoticeKind.InfoRequested, order, note.Text, Table.Clock);

        await Store.AddNoteAsync(
            new DisputeNoteCommit(order, note, MarkAwaitingInfo: true, [notice], AuditFor(AuditActions.DisputeInfoRequested, order.Id)), Ct);

        (await Store.ListNotesAsync(order.Id, Ct)).ShouldBe([note]);
        (await Store.AwaitingInfoAsync([order.Id, "someone-else"], Ct)).ShouldBe([order.Id]);
        (await RowsWithPrefix(DynamoTable.NoticePk(Buyer))).ShouldBe(1);
        (await AuditOf(order.Id)).ShouldHaveSingleItem().Action.ShouldBe("dispute.info_requested");
        (await Table.Orders.FindAsync(order.Id, Ct))!.Version.ShouldBe(order.Version, "a note does not rewrite the order");
    }

    [DynamoLocalFact]
    public async Task An_internal_note_does_not_mark_the_case_as_waiting()
    {
        var order = await StoredDispute();
        var note = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.Note, null, "Sudah cek foto paket.", Table.Clock);

        await Store.AddNoteAsync(new DisputeNoteCommit(order, note, MarkAwaitingInfo: false, [], AuditFor(AuditActions.DisputeNoteAdded, order.Id)), Ct);

        (await Store.AwaitingInfoAsync([order.Id], Ct)).ShouldBeEmpty();
        (await Store.ListNotesAsync(order.Id, Ct)).ShouldHaveSingleItem().Text.ShouldBe("Sudah cek foto paket.");
    }

    [DynamoLocalFact]
    public async Task Notes_come_back_oldest_first()
    {
        var order = await StoredDispute();
        var written = new List<DisputeNote>();
        foreach (var text in new[] { "Catatan satu.", "Catatan dua.", "Catatan tiga." })
        {
            Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
            var note = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.Note, null, text, Table.Clock);
            written.Add(note);
            await Store.AddNoteAsync(new DisputeNoteCommit(order, note, false, [], AuditFor(AuditActions.DisputeNoteAdded, order.Id)), Ct);
        }

        (await Store.ListNotesAsync(order.Id, Ct)).ShouldBe(written);
    }

    [DynamoLocalFact]
    public async Task A_note_on_a_dispute_that_was_just_ruled_on_is_refused_and_leaves_no_audit_row()
    {
        var order = await StoredDispute();
        await Store.ResolveAsync(await RulingFor(order.Id, DisputeResolution.ReleaseToSeller), Ct);
        var auditBefore = (await AuditOf(order.Id)).Count;
        var note = DisputeNote.Create(order.Id, "staff-2", "bob", DisputeNoteKind.Note, null, "Catatan terlambat.", Table.Clock);

        await Should.ThrowAsync<ConcurrencyConflictException>(() =>
            Store.AddNoteAsync(new DisputeNoteCommit(order, note, false, [], AuditFor(AuditActions.DisputeNoteAdded, order.Id, "staff-2", "bob")), Ct));

        (await AuditOf(order.Id)).Count.ShouldBe(auditBefore);
        (await Store.ListNotesAsync(order.Id, Ct)).Select(n => n.Kind).ShouldBe([DisputeNoteKind.Decision]);
    }

    [DynamoLocalFact]
    public async Task Ruling_clears_the_waiting_marker()
    {
        var order = await StoredDispute();
        var note = DisputeNote.Create(order.Id, "staff-1", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Mohon foto resi.", Table.Clock);
        await Store.AddNoteAsync(
            new DisputeNoteCommit(order, note, true, [OrderNotice.Create(Seller, OrderNoticeKind.InfoRequested, order, note.Text, Table.Clock)], AuditFor(AuditActions.DisputeInfoRequested, order.Id)),
            Ct);
        (await Store.AwaitingInfoAsync([order.Id], Ct)).ShouldContain(order.Id);

        await Store.ResolveAsync(await RulingFor(order.Id, DisputeResolution.ReleaseToSeller), Ct);

        (await Store.AwaitingInfoAsync([order.Id], Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Asking_about_a_hundred_and_fifty_orders_at_once_is_answered_in_full()
    {
        var ids = Enumerable.Range(0, 150).Select(i => $"ORDER{i:D4}").ToList();
        var waiting = new[] { ids[0], ids[99], ids[100], ids[149] };
        foreach (var id in waiting)
        {
            await Table.Client.PutItemAsync(Table.TableName, DisputeItems.InfoMarker(
                DisputeNote.Create(id, "staff-1", "alice", DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon foto.", Table.Clock)));
        }

        (await Store.AwaitingInfoAsync(ids, Ct)).OrderBy(x => x, StringComparer.Ordinal).ShouldBe(waiting.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ─── The refund's outcome ────────────────────────────────────────────────────

    [DynamoLocalFact]
    public async Task A_refund_outcome_is_saved_once_and_a_later_attempt_cannot_rewrite_it()
    {
        var order = await StoredDispute();
        await Store.ResolveAsync(await RulingFor(order.Id, DisputeResolution.RefundToBuyer), Ct);
        var refund = (await Store.FindRefundAsync(order.Id, Ct))!;

        refund.RecordOutcome(RefundStatus.PendingGateway, Table.Clock);
        await Store.SaveRefundOutcomeAsync(refund, Ct);
        (await Store.FindRefundAsync(order.Id, Ct))!.Status.ShouldBe(RefundStatus.PendingGateway);

        var other = (await Store.FindRefundAsync(order.Id, Ct))!;
        await Store.SaveRefundOutcomeAsync(Refund.Rehydrate(
            other.Id, other.OrderId, other.OrderNumber, other.BuyerId, other.Amount, other.Reason, other.StaffId, other.StaffName,
            other.CreatedAt, RefundStatus.Simulated, Table.Clock.UtcNow), Ct);

        (await Store.FindRefundAsync(order.Id, Ct))!.Status.ShouldBe(RefundStatus.PendingGateway);
    }

    // ─── Disputes that existed before the queue ──────────────────────────────────

    [DynamoLocalFact]
    public async Task The_backfill_gives_a_dispute_stored_before_the_queue_existed_its_key_and_is_safe_to_repeat()
    {
        var order = await StoredDispute();

        // Rewrite the row the way it was stored before this milestone: a disputed order with no sweep keys.
        var item = OrderItem.ToItem(order);
        item.Remove(DynamoTable.AuctionEndPk);
        item.Remove(DynamoTable.AuctionEndSk);
        await Table.Client.PutItemAsync(Table.TableName, item);
        (await Store.ListOpenAsync(10, Ct)).ShouldBeEmpty("the order is invisible to the queue until it is keyed");

        var backfill = (IOrderSweepBackfill)Table.Orders;
        var updated = 0;
        string? cursor = null;
        do
        {
            var page = await backfill.BackfillAsync(cursor, 25, Ct);
            updated += page.Updated;
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        updated.ShouldBe(3, "the sweep key and the two per-person pointers");
        (await Store.ListOpenAsync(10, Ct)).ShouldHaveSingleItem().Id.ShouldBe(order.Id);

        // A second pass finds nothing left to do, and the order is still whole.
        var again = 0;
        do
        {
            var page = await backfill.BackfillAsync(cursor, 25, Ct);
            again += page.Updated;
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        again.ShouldBe(0);
        (await Table.Orders.FindAsync(order.Id, Ct))!.EscrowState.ShouldBe(EscrowState.Disputed);
    }
}
