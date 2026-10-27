using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Features.Orders;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// <c>POST /api/cases/{orderId}/reply</c>: a buyer's or seller's answer to the team's request for information. Only the side that was
/// asked may answer, the other side never sees it, and a case that was ruled on takes no more replies.
/// </summary>
public sealed class CaseReplyTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string Answer = "Ini video unboxing dan foto label pengiriman, tautan ada di pesan.";

    [Fact]
    public async Task The_side_that_was_asked_can_answer_and_the_case_stops_waiting_on_them()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        (await CasesOf(buyer)).Single().GetProperty("status").GetString().ShouldBe("awaitingInfo");

        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        using var response = await ReplyAsync(buyer, order.Id, Answer);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var mine = (await CasesOf(buyer)).Single();
        mine.GetProperty("status").GetString().ShouldBe("open");
        mine.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).ShouldBe(["infoRequest", "reply"]);
        mine.GetProperty("messages")[1].GetProperty("text").GetString().ShouldBe(Answer);
    }

    [Fact]
    public async Task A_reply_is_never_shown_to_the_other_side()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        (await ReplyAsync(buyer, order.Id, Answer)).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var client = factory.CreateClientAs(seller, "seller");
        using var response = await client.GetAsync("/api/cases");
        var raw = await response.Content.ReadAsStringAsync();

        raw.ShouldNotContain("unboxing");
        raw.ShouldNotContain("\"reply\"");
        (await CasesOf(seller)).Single().GetProperty("messages").EnumerateArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_new_request_after_a_reply_waits_on_that_side_again()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        (await ReplyAsync(buyer, order.Id, Answer)).StatusCode.ShouldBe(HttpStatusCode.OK);

        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Ask(order, DisputeParty.Buyer, minutes: 0);

        (await CasesOf(buyer)).Single().GetProperty("status").GetString().ShouldBe("awaitingInfo");
    }

    [Fact]
    public async Task A_side_that_was_not_asked_cannot_reply()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);

        // The team asked the buyer; the seller has nothing to answer.
        using var response = await ReplyAsync(seller, order.Id, Answer);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("case.no_request_pending");
        factory.Cases.ClearedAwaitingBy(order.Id).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reply_needs_an_unanswered_request_not_just_a_dispute()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);

        using var response = await ReplyAsync(buyer, order.Id, Answer);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("case.no_request_pending");
    }

    [Fact]
    public async Task A_stranger_gets_the_same_not_found_as_for_an_order_that_does_not_exist()
    {
        var order = OpenCase(NewId(), NewId());
        Ask(order, DisputeParty.Buyer, minutes: 1);

        using var forStranger = await ReplyAsync(NewId(), order.Id, Answer);
        using var forNothing = await ReplyAsync(NewId(), EntityId.New(factory.Clock.UtcNow), Answer);
        foreach (var response in new[] { forStranger, forNothing })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await CodeOf(response)).ShouldBe("case.not_found");
        }

        // A malformed id never reaches the handler: the well-formed-id middleware answers 404 first.
        using var forGarbage = await ReplyAsync(NewId(), "not-an-order-id", Answer);
        forGarbage.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        factory.Cases.NotesOf(order.Id).ShouldNotContain(note => note.Kind == DisputeNoteKind.Reply);
    }

    [Fact]
    public async Task A_reply_needs_a_signed_in_caller()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync($"/api/cases/{EntityId.New(factory.Clock.UtcNow)}/reply", new { text = Answer });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_case_that_was_ruled_on_takes_no_more_replies()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromHours(1));
        order.ResolveDispute(DisputeResolution.ReleaseToSeller, factory.Clock);
        factory.Orders.Seed(order);

        using var response = await ReplyAsync(buyer, order.Id, Answer);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(response)).ShouldBe("case.closed");
    }

    [Fact]
    public async Task A_reply_that_is_empty_or_too_long_is_refused_before_anything_is_written()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);

        using var empty = await ReplyAsync(buyer, order.Id, "   ");
        using var tooLong = await ReplyAsync(buyer, order.Id, new string('x', DisputeNote.MaxReplyLength + 1));
        using var exactlyMax = await ReplyAsync(buyer, order.Id, new string('y', DisputeNote.MaxReplyLength));

        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(empty)).ShouldBe("case.reply_required");
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(tooLong)).ShouldBe("case.reply_too_long");
        exactlyMax.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Cases.NotesOf(order.Id).Count(note => note.Kind == DisputeNoteKind.Reply).ShouldBe(1);
    }

    [Fact]
    public async Task The_staff_marker_is_cleared_only_when_every_side_that_was_asked_has_answered()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        var sellerAsked = Ask(order, DisputeParty.Seller, minutes: 2);
        factory.Clock.Advance(TimeSpan.FromMinutes(10));

        (await ReplyAsync(buyer, order.Id, Answer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Cases.ClearedAwaitingBy(order.Id).ShouldBe([null]);

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        (await ReplyAsync(seller, order.Id, "Bukti pengemasan terlampir, lihat foto resi.")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The marker carries the time of the latest request, which is what the clear is conditional on.
        factory.Cases.ClearedAwaitingBy(order.Id).ShouldBe([null, sellerAsked.At]);
    }

    [Fact]
    public async Task A_reply_that_loses_a_race_says_why()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));

        factory.Cases.ConflictOnReply = true;
        try
        {
            using var response = await ReplyAsync(buyer, order.Id, Answer);

            // The case is still disputed, so the cause is the team writing again, not a ruling.
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeOf(response)).ShouldBe("case.reply_conflict");
        }
        finally
        {
            factory.Cases.ConflictOnReply = false;
        }
    }

    [Fact]
    public async Task Sending_the_same_reply_twice_with_one_key_writes_it_once()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Buyer, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var key = Guid.NewGuid().ToString();

        using var first = await ReplyAsync(buyer, order.Id, Answer, key);
        using var second = await ReplyAsync(buyer, order.Id, Answer, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CasesOf(buyer)).Single().GetProperty("messages").EnumerateArray()
            .Count(m => m.GetProperty("kind").GetString() == "reply").ShouldBe(1);
    }

    [Fact]
    public async Task A_reply_is_visible_to_staff_as_a_reply_and_never_as_an_internal_note()
    {
        var (buyer, seller) = (NewId(), NewId());
        var order = OpenCase(buyer, seller);
        Ask(order, DisputeParty.Seller, minutes: 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        (await ReplyAsync(seller, order.Id, Answer)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var written = factory.Cases.NotesOf(order.Id).Single(note => note.Kind == DisputeNoteKind.Reply);

        written.To.ShouldBe(DisputeParty.Seller);
        written.StaffId.ShouldBe(seller);
        written.StaffName.ShouldBe("seller");
        written.Text.ShouldBe(Answer);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static string NewId() => Guid.NewGuid().ToString();

    private async Task<HttpResponseMessage> ReplyAsync(string userId, string orderId, string text, string? key = null)
    {
        using var client = factory.CreateClientAs(userId, "someone");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{orderId}/reply") { Content = JsonContent.Create(new { text }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).GetProperty("code").GetString();

    private async Task<List<JsonElement>> CasesOf(string userId)
    {
        using var client = factory.CreateClientAs(userId, "someone");
        using var response = await client.GetAsync("/api/cases");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return [.. (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray()];
    }

    private Order OpenCase(string buyerId, string sellerId)
    {
        var order = Order.Create(
            buyerId, sellerId,
            [new OrderLine(EntityId.New(factory.Clock.UtcNow), "PSA 10 Charizard", null, Money.FromRupiah(2_000_000), Quantity: 1)],
            new ShippingAddress("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111"),
            FlatRateCouriers.Find("jne")!,
            factory.Clock);
        order.ConfirmPayment(order.GrandTotal, factory.Clock);
        order.OpenDispute("Barang tidak sesuai deskripsi.", buyerId, factory.Clock);
        factory.Orders.Seed(order);
        return order;
    }

    /// <summary>The team asks one side for information, <paramref name="minutes"/> after now.</summary>
    private DisputeNote Ask(Order order, DisputeParty side, int minutes)
    {
        var at = factory.Clock.UtcNow + TimeSpan.FromMinutes(minutes);
        var note = new DisputeNote(EntityId.New(at), order.Id, at, "staff-1", "StaffAyu", DisputeNoteKind.InfoRequest, side, "Mohon kirim bukti tambahan.");
        factory.Cases.AddNote(note);
        return note;
    }
}
