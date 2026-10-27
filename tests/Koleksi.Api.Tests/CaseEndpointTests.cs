using System.Net;
using System.Text.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The public side of disputes: <c>GET /api/cases</c> (a buyer's or seller's own cases) and the refund entries on
/// <c>GET /api/payouts</c>. The rows are the ones the back office writes; here they are seeded through the same domain types.
/// </summary>
public sealed class CaseEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string StaffInternal = "RAHASIA staf: pembeli ini sering menyengketakan barang.";

    // ─── Ownership ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_case_is_listed_for_its_buyer_and_its_seller_and_for_nobody_else()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);

        (await CasesOf(buyer)).Select(c => c.GetProperty("orderId").GetString()).ShouldBe([order.Id]);
        (await CasesOf(seller)).Select(c => c.GetProperty("orderId").GetString()).ShouldBe([order.Id]);

        // A third person, even a signed-in one with disputes of their own, sees neither the case nor anything about it.
        var (otherBuyer, otherSeller) = (NewId(), NewId());
        var theirs = OpenCase(otherBuyer, otherSeller);
        var strangers = await CasesOf(otherBuyer);
        strangers.Select(c => c.GetProperty("orderId").GetString()).ShouldBe([theirs.Id]);
        strangers.ShouldNotContain(c => c.GetProperty("orderNumber").GetString() == order.OrderNumber);
    }

    [Fact]
    public async Task Another_persons_case_cannot_be_asked_for_by_id()
    {
        var order = OpenCase(NewId(), NewId());
        using var stranger = factory.CreateClientAs(NewId(), "stranger");

        // There is no per-case route to probe: the id is not an input anywhere.
        (await stranger.GetAsync($"/api/cases/{order.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/cases?orderId={order.Id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CasesOf(stranger)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cases_need_a_signed_in_caller()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync("/api/cases")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_case_list_and_the_money_list_are_never_cached()
    {
        // Both carry the team's private words to one person; a shared cache or a back button must never replay them to another.
        var (buyer, seller) = (NewId(), NewId());
        OpenCase(buyer, seller);
        using var client = factory.CreateClientAs(buyer, "buyer");

        foreach (var path in new[] { "/api/cases", "/api/payouts" })
        {
            using var response = await client.GetAsync(path);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            response.Headers.CacheControl.ShouldNotBeNull(path);
            response.Headers.CacheControl.NoStore.ShouldBeTrue(path);
        }
    }

    [Fact]
    public async Task A_person_without_disputes_gets_an_empty_list()
    {
        var cases = await CasesOf(NewId());

        cases.ShouldBeEmpty();
    }

    // ─── What is shown, and what is never shown ──────────────────────────────────

    [Fact]
    public async Task A_staff_internal_note_never_reaches_either_side()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        factory.Cases.AddNote(Note(order, DisputeNoteKind.Note, null, StaffInternal));
        factory.Cases.AddNote(Note(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto kerusakan."));

        foreach (var who in new[] { buyer, seller })
        {
            using var client = factory.CreateClientAs(who, "someone");
            using var response = await client.GetAsync("/api/cases");
            var raw = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            raw.ShouldNotContain("RAHASIA");
            raw.ShouldNotContain("staf");
        }
    }

    [Fact]
    public async Task An_information_request_is_shown_to_the_side_it_is_addressed_to_only()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        factory.Cases.AddNote(Note(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto kerusakan."));

        var forBuyer = (await CasesOf(buyer)).Single();
        var forSeller = (await CasesOf(seller)).Single();

        forBuyer.GetProperty("status").GetString().ShouldBe("awaitingInfo");
        forBuyer.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("text").GetString())
            .ShouldBe(["Mohon kirim foto kerusakan."]);

        // The seller is not told the team asked the buyer something: the case just reads as open.
        forSeller.GetProperty("status").GetString().ShouldBe("open");
        forSeller.GetProperty("messages").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_side_stays_awaiting_info_until_the_ruling_whoever_the_team_asked_last()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        factory.Cases.AddNote(Note(order, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Untuk pembeli.", minutes: 1));
        factory.Cases.AddNote(Note(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Untuk penjual.", minutes: 2));

        // A later request to the other side does not clear this side's: nothing records an answer, so each stays pending.
        (await CasesOf(buyer)).Single().GetProperty("status").GetString().ShouldBe("awaitingInfo");
        (await CasesOf(seller)).Single().GetProperty("status").GetString().ShouldBe("awaitingInfo");
    }

    [Fact]
    public async Task A_case_waiting_on_you_is_listed_before_newer_ones()
    {
        var (buyer, seller) = (NewId(), NewId());
        var waiting = OpenCase(buyer, seller);
        factory.Cases.AddNote(Note(waiting, DisputeNoteKind.InfoRequest, DisputeParty.Buyer, "Mohon kirim foto."));
        factory.Clock.Advance(TimeSpan.FromDays(2));
        var newer = OpenCase(buyer, seller);

        (await CasesOf(buyer)).Select(c => c.GetProperty("orderId").GetString()).ShouldBe([waiting.Id, newer.Id]);
        (await CasesOf(seller)).Select(c => c.GetProperty("orderId").GetString()).ShouldBe([newer.Id, waiting.Id]);
    }

    [Fact]
    public async Task A_ruling_shows_its_reason_to_both_sides_and_closes_the_case()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        factory.Cases.AddNote(Note(order, DisputeNoteKind.InfoRequest, DisputeParty.Seller, "Mohon bukti pengemasan.", minutes: 1));
        Rule(order, DisputeResolution.ReleaseToSeller, "Bukti pengemasan sesuai; barang sampai utuh.");

        foreach (var who in new[] { buyer, seller })
        {
            var item = (await CasesOf(who)).Single();

            item.GetProperty("status").GetString().ShouldBe("resolved");
            item.GetProperty("outcome").GetString().ShouldBe("releaseToSeller");
            item.GetProperty("resolvedAt").GetString().ShouldNotBeNullOrEmpty();
            item.GetProperty("messages").EnumerateArray().Last().GetProperty("kind").GetString().ShouldBe("decision");
            item.GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString()
                .ShouldBe("Bukti pengemasan sesuai; barang sampai utuh.");
        }

        // After a ruling nobody is being waited on.
        (await CasesOf(seller)).Single().GetProperty("status").GetString().ShouldNotBe("awaitingInfo");
    }

    [Fact]
    public async Task Only_the_buyer_gets_their_own_complaint_back_and_the_refund_amount()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller, reason: "Kartu tergores di sudut.");
        Rule(order, DisputeResolution.RefundToBuyer, "Foto membuktikan goresan.");
        AddRefund(order, amount: 1_500_000, reason: "Foto membuktikan goresan.");

        var forBuyer = (await CasesOf(buyer)).Single();
        var forSeller = (await CasesOf(seller)).Single();

        forBuyer.GetProperty("yourReason").GetString().ShouldBe("Kartu tergores di sudut.");
        forBuyer.GetProperty("refund").GetProperty("amountRupiah").GetInt64().ShouldBe(1_500_000);
        forBuyer.GetProperty("refund").GetProperty("status").GetString().ShouldBe("recordedPendingGateway");

        // A partial refund reads as a part of what was paid.
        forBuyer.GetProperty("totalRupiah").GetInt64().ShouldBe(order.GrandTotal.Rupiah);
        forBuyer.GetProperty("totalRupiah").GetInt64().ShouldBeGreaterThan(1_500_000);

        // Null properties are left off the wire (the API's JSON options), so "not shown" is absent or null.
        (forSeller.TryGetProperty("yourReason", out var reason) ? reason.ValueKind : JsonValueKind.Null).ShouldBe(JsonValueKind.Null);
        (forSeller.TryGetProperty("refund", out var refund) ? refund.ValueKind : JsonValueKind.Null).ShouldBe(JsonValueKind.Null);
        (await ReadRaw(seller)).ShouldNotContain("Kartu tergores");
        (await ReadRaw(seller)).ShouldNotContain("1500000");
    }

    private async Task<string> ReadRaw(string userId)
    {
        using var client = factory.CreateClientAs(userId, "someone");
        return await (await client.GetAsync("/api/cases")).Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_answer_names_no_account_and_no_staff_member()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Rule(order, DisputeResolution.RefundToBuyer, "Alasan putusan.");

        foreach (var who in new[] { buyer, seller })
        {
            using var client = factory.CreateClientAs(who, "someone");
            var raw = await (await client.GetAsync("/api/cases")).Content.ReadAsStringAsync();

            raw.ShouldNotContain(buyer);
            raw.ShouldNotContain(seller);
            raw.ShouldNotContain("staff", Case.Insensitive);
            raw.ShouldNotContain("StaffAyu");
        }
    }

    // ─── Notices ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reading_the_cases_delivers_the_pending_notice_once()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        var notice = OrderNotice.Create(buyer, OrderNoticeKind.InfoRequested, order, "Tim Koleksi meminta informasi.", factory.Clock);
        factory.Cases.AddNotice(notice);

        (await CasesOf(buyer)).Single().GetProperty("isNew").GetBoolean().ShouldBeTrue();
        factory.Cases.IsPending(notice.Id).ShouldBeFalse();

        (await CasesOf(buyer)).Single().GetProperty("isNew").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Of_several_simultaneous_reads_exactly_one_tells_the_person_it_is_new()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        factory.Cases.AddNotice(OrderNotice.Create(buyer, OrderNoticeKind.InfoRequested, order, "Tim Koleksi meminta informasi.", factory.Clock));

        var reads = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => CasesOf(buyer)));

        reads.Count(cases => cases.Single().GetProperty("isNew").GetBoolean()).ShouldBe(1);
    }

    [Fact]
    public async Task Reading_your_cases_does_not_deliver_someone_elses_notice()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        var sellersNotice = OrderNotice.Create(seller, OrderNoticeKind.DisputeResolvedForSeller, order, "Putusan.", factory.Clock);
        factory.Cases.AddNotice(sellersNotice);

        (await CasesOf(buyer)).Single().GetProperty("isNew").GetBoolean().ShouldBeFalse();
        factory.Cases.IsPending(sellersNotice.Id).ShouldBeTrue();

        (await CasesOf(seller)).Single().GetProperty("isNew").GetBoolean().ShouldBeTrue();
        factory.Cases.IsPending(sellersNotice.Id).ShouldBeFalse();
    }

    // ─── Refunds beside payouts ──────────────────────────────────────────────────

    [Fact]
    public async Task A_buyer_sees_a_refunded_entry_and_a_seller_sees_a_sold_entry_in_the_same_list()
    {
        var (buyer, seller) = (NewId(), NewId());
        var refunded = OpenCase(buyer, seller);
        Rule(refunded, DisputeResolution.RefundToBuyer, "Barang tidak sesuai.");
        AddRefund(refunded, amount: 250_000, reason: "Barang tidak sesuai.");

        // The same person is also a seller whose escrow released: one payout, in the very same list.
        var sold = OpenCase(NewId(), buyer);
        Rule(sold, DisputeResolution.ReleaseToSeller, "Barang sesuai.");
        factory.Orders.Payouts.AddForRelease(Payout.For(sold, factory.Clock));

        using var client = factory.CreateClientAs(buyer, "both.sides");
        var body = await (await client.GetAsync("/api/payouts")).ReadJsonAsync();
        var items = body.GetProperty("items").EnumerateArray().ToList();

        items.Select(i => i.GetProperty("type").GetString()).Order().ShouldBe(["refunded", "sold"]);

        var refund = items.Single(i => i.GetProperty("type").GetString() == "refunded");
        refund.GetProperty("orderNumber").GetString().ShouldBe(refunded.OrderNumber);
        refund.GetProperty("amountRupiah").GetInt64().ShouldBe(250_000);
        refund.GetProperty("orderTotalRupiah").GetInt64().ShouldBe(refunded.GrandTotal.Rupiah);
        refund.GetProperty("reason").GetString().ShouldBe("Barang tidak sesuai.");
        refund.GetProperty("status").GetString().ShouldBe("recordedPendingGateway");

        var payout = items.Single(i => i.GetProperty("type").GetString() == "sold");
        payout.GetProperty("orderNumber").GetString().ShouldBe(sold.OrderNumber);
        payout.GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task A_failing_refund_lookup_does_not_take_the_payouts_down_with_it()
    {
        var (buyer, seller) = (NewId(), NewId());
        var refunded = OpenCase(buyer, seller);
        Rule(refunded, DisputeResolution.RefundToBuyer, "Barang tidak sesuai.");
        AddRefund(refunded, amount: 100_000, reason: "Barang tidak sesuai.");

        // The same person also sold something: their payout must still be listed.
        var sold = OpenCase(NewId(), buyer);
        Rule(sold, DisputeResolution.ReleaseToSeller, "Barang sesuai.");
        factory.Orders.Payouts.AddForRelease(Payout.For(sold, factory.Clock));

        factory.Cases.FailWith = new InvalidOperationException("refund store down");
        try
        {
            using var client = factory.CreateClientAs(buyer, "both.sides");
            using var response = await client.GetAsync("/api/payouts");
            var body = await response.ReadJsonAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.GetProperty("refundsUnavailable").GetBoolean().ShouldBeTrue();
            body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("type").GetString()).ShouldBe(["sold"]);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain("refund store down");
        }
        finally
        {
            factory.Cases.FailWith = null;
        }

        using var healthy = factory.CreateClientAs(buyer, "both.sides");
        (await (await healthy.GetAsync("/api/payouts")).ReadJsonAsync()).GetProperty("refundsUnavailable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task A_refund_is_never_listed_for_the_seller_or_for_a_stranger()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Rule(order, DisputeResolution.RefundToBuyer, "Barang tidak sesuai.");
        AddRefund(order, amount: 100_000, reason: "Barang tidak sesuai.");

        foreach (var who in new[] { seller, NewId() })
        {
            using var client = factory.CreateClientAs(who, "not.the.buyer");
            var body = await (await client.GetAsync("/api/payouts")).ReadJsonAsync();

            body.GetProperty("items").EnumerateArray().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_simulated_refund_is_labelled_simulated_and_none_claims_money_moved()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Rule(order, DisputeResolution.RefundToBuyer, "Barang tidak sesuai.");
        var refund = AddRefund(order, amount: 100_000, reason: "Barang tidak sesuai.");
        refund.RecordOutcome(RefundStatus.Simulated, factory.Clock);

        using var client = factory.CreateClientAs(buyer, "buyer");
        var raw = await (await client.GetAsync("/api/payouts")).Content.ReadAsStringAsync();

        raw.ShouldContain("\"status\":\"simulated\"");
        raw.ShouldNotContain("Paid");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString();

    private async Task<List<JsonElement>> CasesOf(string userId)
    {
        using var client = factory.CreateClientAs(userId, "someone");
        return await CasesOf(client);
    }

    private static async Task<List<JsonElement>> CasesOf(HttpClient client)
    {
        using var response = await client.GetAsync("/api/cases");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray()];
    }

    /// <summary>A paid order with a dispute open on it, as a buyer's complaint leaves it.</summary>
    private Order OpenCase(string buyerId, string sellerId, string reason = "Barang tidak sesuai deskripsi.")
    {
        var order = Order.Create(
            buyerId, sellerId,
            [new OrderLine(EntityId.New(factory.Clock.UtcNow), "PSA 10 Charizard", null, Money.FromRupiah(2_000_000), Quantity: 1)],
            new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
            FlatRateCouriers.Find("jne")!,
            factory.Clock);
        order.ConfirmPayment(order.GrandTotal, factory.Clock);
        order.OpenDispute(reason, buyerId, factory.Clock);
        factory.Orders.Seed(order);
        return order;
    }

    /// <summary>Rules on a case the way the back office does: the order moves, and the decision is written as a note.</summary>
    private void Rule(Order order, DisputeResolution resolution, string reason)
    {
        factory.Clock.Advance(TimeSpan.FromHours(1));
        order.ResolveDispute(resolution, factory.Clock);
        factory.Orders.Seed(order);
        factory.Cases.AddNote(Note(order, DisputeNoteKind.Decision, null, reason, minutes: 90));
    }

    private Refund AddRefund(Order order, long amount, string reason)
    {
        var refund = Refund.Record(order, amount, reason, "staff-1", "StaffAyu", factory.Clock);
        factory.Cases.AddRefund(refund);
        return refund;
    }

    private DisputeNote Note(Order order, DisputeNoteKind kind, DisputeParty? to, string text, int minutes = 0)
    {
        var at = factory.Clock.UtcNow + TimeSpan.FromMinutes(minutes);
        return new DisputeNote(EntityId.New(at), order.Id, at, "staff-1", "StaffAyu", kind, to, text);
    }
}
