using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>The rules behind a staff ruling on a dispute: the reason, the refund record and its limits, the case notes, the notices.</summary>
public sealed class DisputeRulingTests
{
    private const string Buyer = "buyer-sub";
    private const string Seller = "seller-sub";
    private const string Staff = "staff-sub";

    private static readonly CourierSelection Jne = new("jne", "JNE REG", Money.FromRupiah(24_000), 3);

    /// <summary>A paid, shipped, delivered order whose buyer then opened a dispute. Items Rp4.250.000 plus shipping Rp24.000.</summary>
    private static (Order Order, TestClock Clock) Disputed()
    {
        var clock = TestClock.AtEpoch();
        var order = Order.Create(
            Buyer, Seller,
            [new OrderLine("L1", "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            Jne, clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 1, 1)), Seller, clock);
        order.MarkDelivered(clock);
        order.OpenDispute("Kartu yang datang bukan PSA 10 seperti di deskripsi.", Buyer, clock);
        return (order, clock);
    }

    private static Order Refunded(out TestClock clock)
    {
        var (order, c) = Disputed();
        order.ResolveDispute(DisputeResolution.RefundToBuyer, c);
        clock = c;
        return order;
    }

    // ─── The reason ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("abcd")]
    [InlineData("  ab  ")]
    public void A_reason_shorter_than_five_characters_after_trimming_is_refused(string text) =>
        Should.Throw<DomainException>(() => AdminReason.Require(text)).Code.ShouldBe("admin.reason_invalid");

    [Fact]
    public void A_reason_of_five_to_a_thousand_characters_is_accepted_and_trimmed()
    {
        AdminReason.Require("  abcde ").ShouldBe("abcde");
        AdminReason.Require(new string('r', 1000)).Length.ShouldBe(1000);
        Should.Throw<DomainException>(() => AdminReason.Require(new string('r', 1001))).Code.ShouldBe("admin.reason_invalid");
    }

    [Fact]
    public void A_null_reason_or_one_with_a_nul_character_is_refused_but_line_breaks_are_fine()
    {
        Should.Throw<DomainException>(() => AdminReason.Require(null));
        Should.Throw<DomainException>(() => AdminReason.Require("abc\0def"));
        AdminReason.Require("line one\nline two").ShouldBe("line one\nline two");
    }

    // ─── The refund record ───────────────────────────────────────────────────────

    [Fact]
    public void A_refund_records_the_amount_the_reason_and_who_decided()
    {
        var order = Refunded(out var clock);

        var refund = Refund.Record(order, 1_000_000, "Barang tidak sesuai deskripsi.", Staff, " alice ", clock);

        refund.Amount.ShouldBe(Money.FromRupiah(1_000_000));
        refund.OrderId.ShouldBe(order.Id);
        refund.OrderNumber.ShouldBe(order.OrderNumber);
        refund.BuyerId.ShouldBe(Buyer);
        refund.Reason.ShouldBe("Barang tidak sesuai deskripsi.");
        refund.StaffId.ShouldBe(Staff);
        refund.StaffName.ShouldBe("alice");
        refund.CreatedAt.ShouldBe(clock.UtcNow);
        refund.Status.ShouldBe(RefundStatus.Recorded);
        EntityId.IsValid(refund.Id).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-1L, false)]
    [InlineData(1L, true)]
    [InlineData(4_274_000L, true)]
    [InlineData(4_274_001L, false)]
    [InlineData(long.MaxValue, false)]
    public void A_refund_is_from_one_rupiah_up_to_the_order_total_and_no_more(long amount, bool accepted)
    {
        var order = Refunded(out var clock);

        if (accepted)
        {
            Refund.Record(order, amount, "alasan yang cukup", Staff, "alice", clock).Amount.Rupiah.ShouldBe(amount);
        }
        else
        {
            Should.Throw<DomainException>(() => Refund.Record(order, amount, "alasan yang cukup", Staff, "alice", clock))
                .Code.ShouldBe("dispute.refund_amount_invalid");
        }
    }

    [Fact]
    public void The_ceiling_is_what_the_buyer_paid_including_shipping_not_the_items_alone()
    {
        var order = Refunded(out var clock);

        order.ItemsTotal.Rupiah.ShouldBe(4_250_000);
        order.GrandTotal.Rupiah.ShouldBe(4_274_000);
        Should.NotThrow(() => Refund.Record(order, 4_274_000, "ganti rugi penuh", Staff, "alice", clock));
    }

    [Fact]
    public void A_refund_needs_a_reason()
    {
        var order = Refunded(out var clock);

        Should.Throw<DomainException>(() => Refund.Record(order, 1000, "abc", Staff, "alice", clock)).Code.ShouldBe("admin.reason_invalid");
    }

    [Fact]
    public void Only_an_order_a_dispute_ruling_refunded_can_have_a_refund()
    {
        var (stillDisputed, clock) = Disputed();
        Should.Throw<DomainException>(() => Refund.Record(stillDisputed, 1000, "alasan cukup", Staff, "alice", clock))
            .Code.ShouldBe("refund.order_not_refunded");

        var (forSeller, sellerClock) = Disputed();
        forSeller.ResolveDispute(DisputeResolution.ReleaseToSeller, sellerClock);
        Should.Throw<DomainException>(() => Refund.Record(forSeller, 1000, "alasan cukup", Staff, "alice", sellerClock))
            .Code.ShouldBe("refund.order_not_refunded");
    }

    [Fact]
    public void A_refund_moves_from_recorded_once_and_never_back()
    {
        var order = Refunded(out var clock);
        var refund = Refund.Record(order, 5000, "alasan cukup", Staff, "alice", clock);
        clock.Advance(TimeSpan.FromSeconds(5));

        refund.RecordOutcome(RefundStatus.PendingGateway, clock);
        var movedAt = refund.StatusAt;
        clock.Advance(TimeSpan.FromSeconds(5));
        refund.RecordOutcome(RefundStatus.Simulated, clock);

        refund.Status.ShouldBe(RefundStatus.PendingGateway, "a refund that has an outcome keeps it");
        refund.StatusAt.ShouldBe(movedAt);
        Should.Throw<DomainException>(() => refund.RecordOutcome(RefundStatus.Recorded, clock));
    }

    // ─── The remainder of a partial refund ───────────────────────────────────────
    // Order: items Rp4.250.000 + shipping Rp24.000 = Rp4.274.000; fee 5% so a normal sale pays the seller Rp4.037.500.

    private static Refund Partial(Order order, IClock clock, long amount, RemainderChoice? choice = null) =>
        Refund.Record(order, amount, "alasan yang cukup", Staff, "alice", clock, choice);

    [Fact]
    public void A_full_refund_has_no_remainder_and_owes_the_seller_nothing()
    {
        var order = Refunded(out var clock);

        var refund = Partial(order, clock, 4_274_000);

        refund.Remainder.ShouldBe(Money.Zero);
        refund.Disposition.ShouldBe(RemainderDisposition.None);
        refund.SellerPayout.ShouldBe(Money.Zero);
        refund.RemainderPayout(order, clock).ShouldBeNull();
    }

    [Fact]
    public void A_choice_for_a_full_refund_is_refused_because_there_is_nothing_to_decide()
    {
        var order = Refunded(out var clock);

        Should.Throw<DomainException>(() => Partial(order, clock, 4_274_000, new RemainderChoice(RemainderMode.Release)))
            .Code.ShouldBe("dispute.remainder_invalid");
    }

    [Fact]
    public void A_partial_refund_holds_the_remainder_by_default_and_owes_no_one()
    {
        var order = Refunded(out var clock);

        var refund = Partial(order, clock, 1_000_000);

        refund.Remainder.ShouldBe(Money.FromRupiah(3_274_000));
        refund.Disposition.ShouldBe(RemainderDisposition.Held);
        refund.SellerPayout.ShouldBe(Money.Zero);
        refund.SettledAt.ShouldBeNull();
        refund.RemainderPayout(order, clock).ShouldBeNull();
    }

    [Fact]
    public void Choosing_to_hold_is_the_same_as_leaving_the_choice_out_and_takes_no_amount()
    {
        var order = Refunded(out var clock);

        Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Hold)).Disposition.ShouldBe(RemainderDisposition.Held);
        Should.Throw<DomainException>(() => Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Hold, 5_000)))
            .Code.ShouldBe("dispute.remainder_invalid");
    }

    [Fact]
    public void Releasing_the_remainder_pays_the_seller_the_remainder_less_the_fee_at_the_orders_own_rate()
    {
        var order = Refunded(out var clock);

        var refund = Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Release));
        var payout = refund.RemainderPayout(order, clock)!;

        refund.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        refund.SellerPayout.ShouldBe(Money.FromRupiah(3_110_300));
        payout.Amount.ShouldBe(Money.FromRupiah(3_110_300));
        payout.Fee.ShouldBe(Money.FromRupiah(163_700));
        payout.SellerId.ShouldBe(Seller);
        payout.OrderId.ShouldBe(order.Id);
        (refund.Amount + payout.Amount + payout.Fee).ShouldBe(order.GrandTotal, "every rupiah the buyer paid is accounted for");
    }

    [Fact]
    public void A_remainder_cannot_pay_the_seller_more_than_a_normal_sale_would_have()
    {
        var order = Refunded(out var clock);

        // Refunding Rp1 leaves Rp4.273.999, whose 95% would exceed the Rp4.037.500 the sale itself pays.
        var refund = Partial(order, clock, 1, new RemainderChoice(RemainderMode.Release));

        refund.SellerPayout.ShouldBe(order.SellerPayout);
        (refund.Amount + refund.SellerPayout).ShouldBeLessThanOrEqualTo(order.GrandTotal);
    }

    [Fact]
    public void A_release_that_may_not_carry_a_typed_amount()
    {
        var order = Refunded(out var clock);

        Should.Throw<DomainException>(() => Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Release, 100)))
            .Code.ShouldBe("dispute.remainder_invalid");
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(1L, true)]
    [InlineData(3_274_000L, true)]
    [InlineData(3_274_001L, false)]
    public void A_split_gives_the_seller_the_typed_share_from_one_rupiah_up_to_the_remainder(long share, bool accepted)
    {
        var order = Refunded(out var clock);
        var choice = new RemainderChoice(RemainderMode.Split, share);

        if (accepted)
        {
            var refund = Partial(order, clock, 1_000_000, choice);
            refund.Disposition.ShouldBe(RemainderDisposition.Split);
            refund.SellerPayout.Rupiah.ShouldBe(share);
            var payout = refund.RemainderPayout(order, clock)!;
            payout.Amount.Rupiah.ShouldBe(share);
            payout.Fee.Rupiah.ShouldBe(3_274_000 - share, "the platform keeps whatever the seller is not given");
        }
        else
        {
            Should.Throw<DomainException>(() => Partial(order, clock, 1_000_000, choice)).Code.ShouldBe("dispute.remainder_amount_invalid");
        }
    }

    [Fact]
    public void A_split_with_no_amount_is_refused()
    {
        var order = Refunded(out var clock);

        Should.Throw<DomainException>(() => Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Split)))
            .Code.ShouldBe("dispute.remainder_amount_invalid");
    }

    [Fact]
    public void A_split_is_capped_at_what_a_normal_sale_would_pay_even_when_the_remainder_is_larger()
    {
        var order = Refunded(out var clock);

        Should.Throw<DomainException>(() => Partial(order, clock, 1, new RemainderChoice(RemainderMode.Split, 4_037_501)))
            .Code.ShouldBe("dispute.remainder_amount_invalid");
        Should.NotThrow(() => Partial(order, clock, 1, new RemainderChoice(RemainderMode.Split, 4_037_500)));
    }

    [Fact]
    public void A_held_remainder_is_settled_once_by_releasing_or_splitting_it()
    {
        var order = Refunded(out var clock);
        var refund = Partial(order, clock, 1_000_000);
        clock.Advance(TimeSpan.FromHours(3));

        refund.SettleRemainder(order, new RemainderChoice(RemainderMode.Release), clock);

        refund.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        refund.SellerPayout.ShouldBe(Money.FromRupiah(3_110_300));
        refund.SettledAt.ShouldBe(clock.UtcNow);
        refund.RemainderPayout(order, clock)!.Amount.ShouldBe(Money.FromRupiah(3_110_300));

        Should.Throw<DomainException>(() => refund.SettleRemainder(order, new RemainderChoice(RemainderMode.Release), clock))
            .Code.ShouldBe("dispute.remainder_not_held");
    }

    [Fact]
    public void Settling_by_holding_again_is_refused_and_leaves_it_held()
    {
        var order = Refunded(out var clock);
        var refund = Partial(order, clock, 1_000_000);

        Should.Throw<DomainException>(() => refund.SettleRemainder(order, new RemainderChoice(RemainderMode.Hold), clock))
            .Code.ShouldBe("dispute.remainder_invalid");
        refund.Disposition.ShouldBe(RemainderDisposition.Held);
        refund.SettledAt.ShouldBeNull();
    }

    [Fact]
    public void A_refused_settlement_changes_nothing()
    {
        var order = Refunded(out var clock);
        var refund = Partial(order, clock, 1_000_000);

        Should.Throw<DomainException>(() => refund.SettleRemainder(order, new RemainderChoice(RemainderMode.Split, 9_999_999), clock));

        refund.Disposition.ShouldBe(RemainderDisposition.Held);
        refund.SellerPayout.ShouldBe(Money.Zero);
        refund.SettledAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(RemainderMode.Release)]
    [InlineData(RemainderMode.Split)]
    public void There_is_nothing_to_settle_on_a_full_refund_or_one_already_decided(RemainderMode mode)
    {
        var order = Refunded(out var clock);
        var choice = new RemainderChoice(mode, mode == RemainderMode.Split ? 1000 : null);

        var full = Partial(order, clock, 4_274_000);
        Should.Throw<DomainException>(() => full.SettleRemainder(order, choice, clock)).Code.ShouldBe("dispute.remainder_not_held");

        var decided = Partial(order, clock, 1_000_000, new RemainderChoice(RemainderMode.Split, 500_000));
        Should.Throw<DomainException>(() => decided.SettleRemainder(order, choice, clock)).Code.ShouldBe("dispute.remainder_not_held");
    }

    [Fact]
    public void A_refund_stored_before_remainders_existed_reads_as_having_none()
    {
        var order = Refunded(out var clock);

        var old = Refund.Rehydrate(
            "R1", order.Id, order.OrderNumber, Buyer, Money.FromRupiah(1_000_000), "alasan", Staff, "alice",
            clock.UtcNow, RefundStatus.Recorded, clock.UtcNow);

        old.Remainder.ShouldBe(Money.Zero);
        old.Disposition.ShouldBe(RemainderDisposition.None);
        old.SellerPayout.ShouldBe(Money.Zero);
        old.RemainderPayout(order, clock).ShouldBeNull();
        Should.Throw<DomainException>(() => old.SettleRemainder(order, new RemainderChoice(RemainderMode.Release), clock))
            .Code.ShouldBe("dispute.remainder_not_held");
    }

    [Fact]
    public void A_remainder_payout_only_exists_for_an_order_refunded_by_a_ruling()
    {
        var (disputed, clock) = Disputed();
        Should.Throw<DomainException>(() => Payout.ForRemainder(disputed, Money.FromRupiah(1000), Money.Zero, clock))
            .Code.ShouldBe("payout.order_not_refunded");

        var (forSeller, sellerClock) = Disputed();
        forSeller.ResolveDispute(DisputeResolution.ReleaseToSeller, sellerClock);
        Should.Throw<DomainException>(() => Payout.ForRemainder(forSeller, Money.FromRupiah(1000), Money.Zero, sellerClock))
            .Code.ShouldBe("payout.order_not_refunded");

        var refunded = Refunded(out var refundClock);
        Should.Throw<DomainException>(() => Payout.ForRemainder(refunded, Money.Zero, Money.Zero, refundClock))
            .Code.ShouldBe("payout.amount_invalid");
    }

    // ─── The ruling itself, through the existing domain method ──────────────────

    [Fact]
    public void Ruling_for_the_seller_releases_the_order_and_owes_the_seller_exactly_once()
    {
        var (order, clock) = Disputed();

        order.ResolveDispute(DisputeResolution.ReleaseToSeller, clock);
        var payout = Payout.For(order, clock);

        order.EscrowState.ShouldBe(EscrowState.Released);
        payout.Amount.ShouldBe(order.SellerPayout);
        payout.OrderId.ShouldBe(order.Id);
        payout.Status.ShouldBe(PayoutStatus.Pending);
    }

    [Fact]
    public void A_second_ruling_on_the_same_order_is_refused_by_the_domain()
    {
        var (order, clock) = Disputed();
        order.ResolveDispute(DisputeResolution.RefundToBuyer, clock);

        Should.Throw<DomainException>(() => order.ResolveDispute(DisputeResolution.ReleaseToSeller, clock)).Code.ShouldBe("order.no_open_dispute");
    }

    // ─── Case notes and notices ─────────────────────────────────────────────────

    [Fact]
    public void An_internal_note_names_no_one_and_a_request_names_exactly_one_side()
    {
        var clock = TestClock.AtEpoch();

        var note = DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Note, null, "Pembeli sudah dihubungi lewat chat.", clock);
        var request = DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim video unboxing.", clock);

        note.To.ShouldBeNull();
        request.To.ShouldBe(DisputeParty.Buyer);
        Should.Throw<DomainException>(() => DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Note, DisputeParty.Buyer, "catatan internal", clock))
            .Code.ShouldBe("admin.note_invalid");
        Should.Throw<DomainException>(() => DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.InfoRequest, null, "mohon kirim bukti", clock))
            .Code.ShouldBe("admin.note_invalid");
    }

    [Fact]
    public void A_reply_names_the_side_that_wrote_it_and_cannot_be_made_as_a_staff_note()
    {
        var clock = TestClock.AtEpoch();

        var reply = DisputeNote.CreateReply("O1", "user-7", DisputeParty.Seller, "  Bukti pengemasan terlampir.  ", clock);

        reply.Kind.ShouldBe(DisputeNoteKind.Reply);
        reply.To.ShouldBe(DisputeParty.Seller);
        reply.StaffId.ShouldBe("user-7");
        reply.StaffName.ShouldBe("seller");
        reply.Text.ShouldBe("Bukti pengemasan terlampir.");
        Should.Throw<DomainException>(() => DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Reply, DisputeParty.Buyer, "Pura-pura balasan.", clock))
            .Code.ShouldBe("admin.note_invalid");
    }

    [Fact]
    public void A_reply_may_be_short_but_not_empty_or_longer_than_a_complaint()
    {
        var clock = TestClock.AtEpoch();

        DisputeNote.CreateReply("O1", "user-7", DisputeParty.Buyer, "Ya.", clock).Text.ShouldBe("Ya.");
        DisputeNote.CreateReply("O1", "user-7", DisputeParty.Buyer, new string('a', DisputeNote.MaxReplyLength), clock);
        foreach (var bad in new[] { null, "", "   ", new string('a', DisputeNote.MaxReplyLength + 1), "ada\0nul" })
        {
            Should.Throw<DomainException>(() => DisputeNote.CreateReply("O1", "user-7", DisputeParty.Buyer, bad, clock))
                .Code.ShouldBe("case.reply_invalid");
        }
    }

    [Fact]
    public void A_note_needs_a_real_text_and_notes_sort_by_time_through_their_ids()
    {
        var clock = TestClock.AtEpoch();
        Should.Throw<DomainException>(() => DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Note, null, "ok", clock))
            .Code.ShouldBe("admin.reason_invalid");

        var first = DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Note, null, "catatan pertama", clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = DisputeNote.Create("O1", Staff, "alice", DisputeNoteKind.Note, null, "catatan kedua", clock);

        string.CompareOrdinal(second.Id, first.Id).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_notice_is_pending_text_for_one_person_about_one_order()
    {
        var (order, clock) = Disputed();

        var notice = OrderNotice.Create(Buyer, OrderNoticeKind.DisputeResolvedForBuyer, order, "Pengembalian dana disetujui.", clock);

        notice.UserId.ShouldBe(Buyer);
        notice.OrderNumber.ShouldBe(order.OrderNumber);
        notice.Kind.ShouldBe(OrderNoticeKind.DisputeResolvedForBuyer);
        Should.Throw<DomainException>(() => OrderNotice.Create(Buyer, OrderNoticeKind.InfoRequested, order, "x", clock));
    }

    // ─── Holidays ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_holiday_needs_a_real_date_a_name_and_a_kind()
    {
        Holiday.Create(" 2026-12-25 ", " Natal ", HolidayKind.National).ShouldBe(new Holiday("2026-12-25", "Natal", HolidayKind.National));

        foreach (var badDate in new[] { "", "2026-02-30", "2026-13-01", "26-01-01", "2026/01/01", "2026-1-1", "2019-12-31", "2101-01-01", "abcd-ef-gh" })
        {
            Should.Throw<DomainException>(() => Holiday.Create(badDate, "Nama", HolidayKind.National), badDate)
                .Code.ShouldBe("admin.holiday_invalid");
        }

        Should.Throw<DomainException>(() => Holiday.Create("2026-12-25", "  ", HolidayKind.National));
        Should.Throw<DomainException>(() => Holiday.Create("2026-12-25", new string('n', 121), HolidayKind.National));
        Should.Throw<DomainException>(() => Holiday.Create("2026-12-25", "bad\nname", HolidayKind.National));
        Should.Throw<DomainException>(() => Holiday.Create("2026-12-25", "Nama", (HolidayKind)9));
    }

    [Fact]
    public void A_leap_day_is_a_real_date_only_in_a_leap_year()
    {
        Should.NotThrow(() => Holiday.Create("2028-02-29", "Hari kabisat", HolidayKind.National));
        Should.Throw<DomainException>(() => Holiday.Create("2027-02-29", "Bukan kabisat", HolidayKind.National));
    }

    [Fact]
    public void The_years_a_request_may_ask_for_are_bounded()
    {
        Holiday.RequireYear(2026).ShouldBe(2026);
        Should.Throw<DomainException>(() => Holiday.RequireYear(1999)).Code.ShouldBe("admin.holiday_invalid");
        Should.Throw<DomainException>(() => Holiday.RequireYear(3000));
    }

    [Fact]
    public void The_2026_seed_is_the_decrees_17_national_days_and_8_collective_leave_days()
    {
        var seed = HolidaySeed.Year2026;

        seed.Count(h => h.Kind == HolidayKind.National).ShouldBe(17);
        seed.Count(h => h.Kind == HolidayKind.CollectiveLeave).ShouldBe(8);
        seed.Select(h => h.Date).Distinct().Count().ShouldBe(25, "no date twice");
        seed.All(h => h.Year == 2026 && Holiday.TryParseDate(h.Date, out _)).ShouldBeTrue();
        seed.Select(h => h.Date).ShouldBe(seed.Select(h => h.Date).OrderBy(d => d, StringComparer.Ordinal).ToList(), "listed in date order");
    }

    [Theory]
    [InlineData("2026-03-18", HolidayKind.CollectiveLeave)]
    [InlineData("2026-03-19", HolidayKind.National)]
    [InlineData("2026-03-20", HolidayKind.CollectiveLeave)]
    [InlineData("2026-03-21", HolidayKind.National)]
    [InlineData("2026-03-22", HolidayKind.National)]
    [InlineData("2026-03-23", HolidayKind.CollectiveLeave)]
    [InlineData("2026-03-24", HolidayKind.CollectiveLeave)]
    [InlineData("2026-05-27", HolidayKind.National)]
    [InlineData("2026-05-28", HolidayKind.CollectiveLeave)]
    [InlineData("2026-12-24", HolidayKind.CollectiveLeave)]
    [InlineData("2026-12-25", HolidayKind.National)]
    public void The_dates_news_sites_disagree_about_are_the_decrees(string date, HolidayKind kind) =>
        HolidaySeed.Year2026.Single(h => h.Date == date).Kind.ShouldBe(kind);

    [Fact]
    public void The_2027_seed_is_the_decrees_18_national_days_and_8_collective_leave_days()
    {
        var seed = HolidaySeed.Year2027;

        seed.Count(h => h.Kind == HolidayKind.National).ShouldBe(18);
        seed.Count(h => h.Kind == HolidayKind.CollectiveLeave).ShouldBe(8);
        seed.Select(h => h.Date).Distinct().Count().ShouldBe(26, "no date twice");
        seed.All(h => h.Year == 2027 && Holiday.TryParseDate(h.Date, out _)).ShouldBeTrue();
        seed.Select(h => h.Date).ToList().ShouldBe(seed.Select(h => h.Date).OrderBy(d => d, StringComparer.Ordinal).ToList(), "listed in date order");
        seed.All(h => h.Name.Length is > 0 and <= Holiday.MaxNameLength).ShouldBeTrue();
    }

    [Fact]
    public void The_2027_seed_has_nothing_outside_the_decree()
    {
        // The decree's dates, typed out again independently of the seed so a slip in one shows against the other.
        string[] national =
        [
            "01-01", "01-05", "02-06", "03-08", "03-10", "03-11", "03-26", "03-28", "05-01", "05-06", "05-17", "05-20", "06-01", "06-06",
            "08-15", "08-17", "12-25", "12-26",
        ];
        string[] collective = ["02-05", "03-09", "03-12", "03-15", "03-25", "05-18", "05-19", "12-24"];

        HolidaySeed.Year2027.Where(h => h.Kind == HolidayKind.National).Select(h => h.Date[5..]).ToList().ShouldBe(national);
        HolidaySeed.Year2027.Where(h => h.Kind == HolidayKind.CollectiveLeave).Select(h => h.Date[5..]).ToList().ShouldBe(collective);
    }

    [Fact]
    public void Every_seeded_year_fits_one_transaction_and_keeps_to_its_own_year()
    {
        HolidaySeed.ByYear.Keys.ShouldBe([2026, 2027], ignoreOrder: true);
        foreach (var (year, seed) in HolidaySeed.ByYear)
        {
            seed.All(h => h.Year == year).ShouldBeTrue(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
            (seed.Count + 1 + 3).ShouldBeLessThanOrEqualTo(100, year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void The_seed_has_nothing_outside_the_decree()
    {
        // The decree's dates, typed out again independently of the seed so a slip in one shows against the other.
        string[] national = ["01-01", "01-16", "02-17", "03-19", "03-21", "03-22", "04-03", "04-05", "05-01", "05-14", "05-27", "05-31", "06-01", "06-16", "08-17", "08-25", "12-25"];
        string[] collective = ["02-16", "03-18", "03-20", "03-23", "03-24", "05-15", "05-28", "12-24"];

        HolidaySeed.Year2026.Where(h => h.Kind == HolidayKind.National).Select(h => h.Date[5..]).ToList().ShouldBe(national);
        HolidaySeed.Year2026.Where(h => h.Kind == HolidayKind.CollectiveLeave).Select(h => h.Date[5..]).ToList().ShouldBe(collective);
    }
}
