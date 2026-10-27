using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>
/// The back office's Milestone 1 routes through the real pipeline: gate, JWT, per-capability policy, rate limiter, handlers, exception
/// mapping. Only the storage adapters are in memory (see BackofficeFakes); the real ones are proved against DynamoDB Local.
/// </summary>
public sealed class BackofficeTests
{
    private const string Buyer = "buyer-sub-0001";
    private const string Seller = "seller-sub-0002";

    /// <summary>Items Rp4.250.000 plus shipping Rp24.000.</summary>
    private const long OrderTotal = 4_274_000;

    // ─── Who may call what ───────────────────────────────────────────────────────

    public static TheoryData<string, string, string, string?> Routes => new()
    {
        { "admin.overview.read", "GET", "/api/admin/overview", null },
        { "admin.disputes.read", "GET", "/api/admin/disputes?status=open", null },
        { "admin.disputes.read", "GET", "/api/admin/disputes/0000000000000000000000000A", null },
        { "admin.disputes.note", "POST", "/api/admin/disputes/0000000000000000000000000A/notes", """{"text":"catatan internal"}""" },
        { "admin.disputes.resolve", "POST", "/api/admin/disputes/0000000000000000000000000A/resolve", """{"resolution":"ReleaseToSeller","reason":"alasan yang cukup"}""" },
        { "admin.disputes.resolve", "POST", "/api/admin/disputes/0000000000000000000000000A/settle-remainder", """{"mode":"release","reason":"alasan yang cukup"}""" },
        { "admin.holidays.read", "GET", "/api/admin/holidays", null },
        { "admin.holidays.edit", "POST", "/api/admin/holidays", """{"date":"2026-12-31","name":"Tes","kind":"national"}""" },
        { "admin.holidays.edit", "POST", "/api/admin/holidays/2026-01-01/remove", "{}" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Without_a_token_every_route_is_refused(string capability, string method, string path, string? body)
    {
        _ = capability;
        using var factory = new AdminApiFactory();
        using var client = factory.CreateWebTierClient();

        using var response = await Send(client, method, path, body);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Without_the_web_tiers_secret_every_route_answers_404(string capability, string method, string path, string? body)
    {
        _ = capability;
        using var factory = new AdminApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Tokens.Access(factory.Key));

        using var response = await Send(client, method, path, body);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_token_from_the_public_pool_is_refused_on_every_route(string capability, string method, string path, string? body)
    {
        _ = capability;
        using var factory = new AdminApiFactory();
        var token = Tokens.Access(factory.Key, new TokenSpec { Issuer = AdminApiFactory.PublicIssuer, ClientId = AdminApiFactory.PublicClientId });
        using var client = factory.CreateWebTierClientWithToken(token);

        using var response = await Send(client, method, path, body);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_route_whose_capability_is_not_granted_is_forbidden_and_changes_nothing(string capability, string method, string path, string? body)
    {
        // Every route names its own capability, so taking just that one away must stop just that route. Done by replacing the
        // named policy with one that denies, which is what a later role split would do for a staff member without it.
        using var parent = new AdminApiFactory();
        using var factory = parent.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddAuthorizationBuilder().AddPolicy(capability, policy => policy.RequireAssertion(_ => false))));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Koleksi-Admin-Internal", AdminApiFactory.InternalSecret);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Tokens.Access(parent.Key));

        using var response = await Send(client, method, path, body);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        parent.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Only_a_signed_in_member_of_staff_holds_a_capability_and_only_a_named_one()
    {
        var staff = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", "x")], "test"));
        var stranger = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());

        foreach (var capability in Koleksi.AdminApi.Security.AdminCapabilities.All)
        {
            Koleksi.AdminApi.Security.StaffCapabilities.IsGranted(staff, capability).ShouldBeTrue(capability);
            Koleksi.AdminApi.Security.StaffCapabilities.IsGranted(stranger, capability).ShouldBeFalse(capability);
        }

        Koleksi.AdminApi.Security.StaffCapabilities.IsGranted(staff, "admin.something.else").ShouldBeFalse();
    }

    // ─── Overview ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_overview_counts_open_disputes_and_leaves_every_other_figure_empty()
    {
        using var factory = new AdminApiFactory();
        var older = factory.Dispute(openedAt: factory.Clock.UtcNow.AddDays(-5));
        var newer = factory.Dispute(openedAt: factory.Clock.UtcNow.AddDays(-1));
        using var client = factory.CreateStaffClient();

        var overview = await client.GetFromJsonAsync<JsonElement>("/api/admin/overview");

        overview.GetProperty("openDisputes").GetInt32().ShouldBe(2);
        overview.GetProperty("disputeOpenedAt").EnumerateArray().Select(e => e.GetDateTimeOffset()).ToList()
            .ShouldBe([older.Dispute!.OpenedAt, newer.Dispute!.OpenedAt], ignoreOrder: true);

        // Only the dispute fields are real in this milestone; the rest are zero or empty by design.
        foreach (var zero in new[] { "openReports", "seriousReports", "openAppeals", "pendingIdChecks", "failedPayouts", "pendingPayouts", "activeListings" })
        {
            overview.GetProperty(zero).GetInt32().ShouldBe(0, zero);
        }

        overview.GetProperty("daily").GetArrayLength().ShouldBe(0);
        overview.GetProperty("last30").GetProperty("gmv").GetInt64().ShouldBe(0);
        overview.GetProperty("previous30").GetProperty("orders").GetInt32().ShouldBe(0);
    }

    // ─── The queue ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_open_queue_lists_the_oldest_dispute_first_and_names_both_sides()
    {
        using var factory = new AdminApiFactory();
        factory.Person(Buyer, "pembeli");
        factory.Person(Seller, "penjual");
        var newer = factory.Dispute(openedAt: factory.Clock.UtcNow.AddDays(-1));
        var older = factory.Dispute(openedAt: factory.Clock.UtcNow.AddDays(-6));
        using var client = factory.CreateStaffClient();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/admin/disputes?status=open");

        list.EnumerateArray().Select(e => e.GetProperty("orderId").GetString()).ToList().ShouldBe([older.Id, newer.Id]);
        var first = list[0];
        first.GetProperty("orderNumber").GetString().ShouldBe(older.OrderNumber);
        first.GetProperty("buyer").GetProperty("handle").GetString().ShouldBe("pembeli");
        first.GetProperty("seller").GetProperty("handle").GetString().ShouldBe("penjual");
        first.GetProperty("total").GetInt64().ShouldBe(OrderTotal);
        first.GetProperty("status").GetString().ShouldBe("open");
        first.TryGetProperty("resolution", out _).ShouldBeFalse("an open dispute has no resolution to show");
    }

    [Fact]
    public async Task The_queue_can_be_narrowed_by_order_number_or_an_exact_handle_and_not_by_part_of_one()
    {
        using var factory = new AdminApiFactory();
        factory.Person(Buyer, "pembeli");
        factory.Person(Seller, "penjual");
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        (await Ids(client, $"q={order.OrderNumber}")).ShouldBe([order.Id]);
        (await Ids(client, $"q={order.OrderNumber.ToLowerInvariant()}")).ShouldBe([order.Id]);
        (await Ids(client, "q=pembeli")).ShouldBe([order.Id]);
        (await Ids(client, "q=@penjual")).ShouldBe([order.Id]);
        (await Ids(client, "q=pembel")).ShouldBeEmpty("a handle is matched exactly, so part of one finds nobody");
        (await Ids(client, "q=orang-lain")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("status=pending")]
    [InlineData("status=")]
    public async Task An_unknown_status_is_a_coded_400(string query)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/disputes?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.dispute_query_invalid");
    }

    [Fact]
    public async Task A_too_long_search_is_a_coded_400()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/disputes?status=open&q={new string('x', 101)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.dispute_query_invalid");
    }

    // ─── One case ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_case_carries_the_order_the_complaint_the_shipment_and_each_sides_earlier_disputes()
    {
        using var factory = new AdminApiFactory();
        factory.Person(Buyer, "pembeli");
        factory.Person(Seller, "penjual");

        // The buyer lost one earlier dispute (ruled for the seller); the seller lost one earlier dispute (ruled for another buyer).
        var earlierBuyerLoss = factory.Dispute(buyer: Buyer, seller: "another-seller");
        earlierBuyerLoss.ResolveDispute(DisputeResolution.ReleaseToSeller, factory.Clock);
        var earlierSellerLoss = factory.Dispute(buyer: "another-buyer", seller: Seller);
        earlierSellerLoss.ResolveDispute(DisputeResolution.RefundToBuyer, factory.Clock);
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");

        found.GetProperty("orderId").GetString().ShouldBe(order.Id);
        found.GetProperty("reason").GetString().ShouldBe(order.Dispute!.Reason);
        found.GetProperty("status").GetString().ShouldBe("open");
        found.GetProperty("escrowState").GetString().ShouldBe("Disputed");
        found.GetProperty("itemsTotal").GetInt64().ShouldBe(4_250_000);
        found.GetProperty("shippingCost").GetInt64().ShouldBe(24_000);
        found.GetProperty("total").GetInt64().ShouldBe(OrderTotal);
        found.GetProperty("lines")[0].GetProperty("title").GetString().ShouldBe("PSA 10 Charizard VMAX");
        found.GetProperty("courier").GetProperty("service").GetString().ShouldBe("JNE REG");
        found.GetProperty("proof").GetProperty("parcelPhoto").GetBoolean().ShouldBeTrue();
        found.GetProperty("shipToCity").GetString().ShouldBe("Jakarta Selatan");

        var history = found.GetProperty("history");
        history.GetProperty("buyerDisputes").GetInt32().ShouldBe(1);
        history.GetProperty("buyerLost").GetInt32().ShouldBe(1);
        history.GetProperty("sellerDisputes").GetInt32().ShouldBe(1);
        history.GetProperty("sellerLost").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task History_counts_every_earlier_dispute_however_many_orders_the_person_has()
    {
        using var factory = new AdminApiFactory();

        // 250 earlier orders for the buyer (more than two pages), a dispute among the very oldest, and one among the newest.
        for (var i = 0; i < 250; i++)
        {
            var past = factory.Delivered(Buyer, "some-seller-" + i);
            if (i is 0 or 249)
            {
                past.OpenDispute("Dulu juga bermasalah.", Buyer, factory.Clock);
                past.ResolveDispute(DisputeResolution.ReleaseToSeller, factory.Clock);
                factory.Orders.Commit(past);
            }

            factory.Clock.UtcNow = factory.Clock.UtcNow.AddMinutes(1);
        }

        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");

        found.GetProperty("history").GetProperty("buyerDisputes").GetInt32().ShouldBe(2);
        found.GetProperty("history").GetProperty("buyerLost").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_case_shows_only_this_orders_chat_and_reading_it_is_audited()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        await factory.Say(order, Buyer, "Halo, slab-nya asli?");
        await factory.Say(order, Seller, "Asli kak.");

        // A thread between the same two people about a different listing, and one between the buyer and a third party: neither is shown.
        await factory.SayAbout("another-listing", Buyer, Seller, Buyer, "Soal barang lain.");
        await factory.SayAbout(order.Lines[0].ListingId, "stranger", Seller, "stranger", "Bukan percakapan ini.");
        using var client = factory.CreateStaffClient();

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");

        var chat = found.GetProperty("chat").EnumerateArray().ToList();
        chat.Select(l => l.GetProperty("text").GetString()).ToList().ShouldBe(["Halo, slab-nya asli?", "Asli kak."]);
        chat.Select(l => l.GetProperty("from").GetString()).ToList().ShouldBe(["buyer", "seller"]);

        var viewed = factory.Audit.Entries.ShouldHaveSingleItem();
        viewed.Action.ShouldBe("dispute.chat_viewed");
        viewed.TargetType.ShouldBe("order");
        viewed.TargetId.ShouldBe(order.Id);
        viewed.ActorId.ShouldBe(AdminApiFactory.StaffAlice);
    }

    [Fact]
    public async Task Opening_a_case_with_no_chat_writes_no_audit_entry()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/disputes/{order.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0000000000000000000000000A")]
    [InlineData("not-an-order-id")]
    [InlineData("%20")]
    public async Task A_dispute_that_does_not_exist_is_a_404_with_its_code(string id)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/disputes/{id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Code(response)).ShouldBe("dispute.not_found");
    }

    [Fact]
    public async Task An_order_that_was_never_disputed_is_not_a_dispute()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Delivered();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/disputes/{order.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Code(response)).ShouldBe("dispute.not_found");
    }

    // ─── Notes and information requests ──────────────────────────────────────────

    [Fact]
    public async Task An_internal_note_is_stored_and_audited_and_tells_nobody()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Sudah cek foto paket." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var note = factory.Disputes.Notes.ShouldHaveSingleItem();
        note.Kind.ShouldBe(DisputeNoteKind.Note);
        note.StaffName.ShouldBe("alice");
        factory.Disputes.Notices.ShouldBeEmpty();

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("dispute.note_added");
        entry.TargetId.ShouldBe(order.Id);
        entry.Reason.ShouldBe("Sudah cek foto paket.");

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");
        found.GetProperty("status").GetString().ShouldBe("open");
        found.GetProperty("notes")[0].GetProperty("kind").GetString().ShouldBe("note");
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("seller")]
    public async Task Asking_a_side_for_information_makes_the_case_wait_and_notifies_that_side_only(string side)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Mohon kirim video unboxing.", to = side });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var notice = factory.Disputes.Notices.ShouldHaveSingleItem();
        notice.UserId.ShouldBe(side == "buyer" ? Buyer : Seller);
        notice.Kind.ShouldBe(OrderNoticeKind.InfoRequested);
        factory.Audit.Entries.ShouldHaveSingleItem().Action.ShouldBe("dispute.info_requested");

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");
        found.GetProperty("status").GetString().ShouldBe("awaitingInfo");
        found.GetProperty("notes")[0].GetProperty("kind").GetString().ShouldBe("infoRequest");
        found.GetProperty("notes")[0].GetProperty("to").GetString().ShouldBe(side);
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("seller")]
    public async Task A_reply_from_a_side_is_shown_to_staff_as_a_reply_with_the_side_that_wrote_it(string side)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        var author = side == "buyer" ? Buyer : Seller;
        factory.Disputes.SeedNote(DisputeNote.CreateReply(
            order.Id, author, side == "buyer" ? DisputeParty.Buyer : DisputeParty.Seller, "Ini video unboxing yang diminta.", factory.Clock));

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");

        var note = found.GetProperty("notes").EnumerateArray().Single();
        note.GetProperty("kind").GetString().ShouldBe("reply");
        note.GetProperty("to").GetString().ShouldBe(side);
        note.GetProperty("text").GetString().ShouldBe("Ini video unboxing yang diminta.");

        // Not a staff note: its "staff name" is the side, and the case is not waiting on anyone because of it.
        found.GetProperty("status").GetString().ShouldBe("open");
    }

    [Theory]
    [InlineData("""{"text":"abcd"}""", "admin.reason_invalid")]
    [InlineData("""{"text":"   "}""", "admin.reason_invalid")]
    [InlineData("""{}""", "admin.reason_invalid")]
    [InlineData("""{"text":"catatan yang cukup","to":"admin"}""", "admin.note_invalid")]
    public async Task A_bad_note_is_a_coded_400_and_stores_nothing(string body, string code)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Raw(client, $"/api/admin/disputes/{order.Id}/notes", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe(code);
        factory.Disputes.Notes.ShouldBeEmpty();
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_note_over_a_thousand_characters_is_refused()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = new string('x', 1001) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.reason_invalid");
    }

    [Fact]
    public async Task A_note_on_a_resolved_dispute_is_refused()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Bukti penjual cukup." })).Dispose();
        var before = factory.Audit.Entries.Count;

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Catatan terlambat." });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(response)).ShouldBe("dispute.already_resolved");
        factory.Audit.Entries.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Sending_the_same_note_twice_with_one_idempotency_key_stores_it_once()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();

        using var first = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Catatan satu kali." }, key);
        using var second = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Catatan satu kali." }, key);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Disputes.Notes.Count.ShouldBe(1);
        factory.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_audit_write_fails_the_note_is_not_stored()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        factory.Audit.FailNextAppend = true;

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Catatan yang gagal.", to = "buyer" });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        factory.Disputes.Notes.ShouldBeEmpty();
        factory.Disputes.Notices.ShouldBeEmpty();
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Buyer)]
    [InlineData(Seller)]
    public async Task Staff_cannot_act_on_a_dispute_they_are_a_party_to(string partyId)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient(partyId, "alice");

        using var note = await Post(client, $"/api/admin/disputes/{order.Id}/notes", new { text = "Catatan sendiri." });
        using var ruling = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Putusan sendiri." });

        note.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        ruling.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Code(ruling)).ShouldBe("admin.cannot_act_on_self");
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    // ─── Ruling for the buyer: a refund ──────────────────────────────────────────

    [Fact]
    public async Task Refunding_part_of_the_total_records_the_refund_the_ruling_both_notices_and_one_audit_entry()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Slab bukan PSA 10, refund sebagian.", refundAmount = 1_500_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.StateOf(order).ShouldBe(EscrowState.Refunded);

        var refund = factory.Disputes.RefundFor(order.Id).ShouldNotBeNull();
        refund.Amount.Rupiah.ShouldBe(1_500_000);
        refund.Reason.ShouldBe("Slab bukan PSA 10, refund sebagian.");
        refund.StaffId.ShouldBe(AdminApiFactory.StaffAlice);
        refund.StaffName.ShouldBe("alice");
        refund.CreatedAt.ShouldBe(factory.Clock.UtcNow);
        refund.Status.ShouldBe(RefundStatus.PendingGateway, "no gateway exists, so the refund is recorded and no money has moved");
        factory.Refunds.Calls.ShouldBe(1);
        factory.Disputes.Payouts.ShouldBeEmpty("a refund owes the seller nothing");

        factory.Disputes.Notices.Select(n => n.UserId).ToList().ShouldBe([Buyer, Seller], ignoreOrder: true);
        factory.Disputes.Notices.ShouldAllBe(n => n.Kind == OrderNoticeKind.DisputeResolvedForBuyer);

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("dispute.resolved_for_buyer");
        entry.TargetId.ShouldBe(order.Id);
        entry.ActorName.ShouldBe("alice");
        entry.Reason.ShouldBe("Slab bukan PSA 10, refund sebagian.");
        entry.Before.ShouldBe("Disputed");
        entry.After.ShouldNotBeNull().ShouldContain("Rp1.500.000");

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");
        found.GetProperty("status").GetString().ShouldBe("resolved");
        found.GetProperty("resolution").GetString().ShouldBe("RefundToBuyer");
        found.GetProperty("refund").GetProperty("amount").GetInt64().ShouldBe(1_500_000);
        found.GetProperty("refund").GetProperty("status").GetString().ShouldBe("recordedPendingGateway");
        found.GetProperty("refund").GetProperty("staffName").GetString().ShouldBe("alice");
        found.GetProperty("notes").EnumerateArray().Last().GetProperty("text").GetString().ShouldBe("Slab bukan PSA 10, refund sebagian.");
    }

    [Fact]
    public async Task The_whole_order_total_can_be_refunded_but_not_one_rupiah_more()
    {
        using var factory = new AdminApiFactory();
        var tooMuch = factory.Dispute();
        var exact = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var refused = await Post(
            client, $"/api/admin/disputes/{tooMuch.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund melebihi total.", refundAmount = OrderTotal + 1 });
        using var accepted = await Post(
            client, $"/api/admin/disputes/{exact.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund penuh.", refundAmount = OrderTotal });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(refused)).ShouldBe("dispute.refund_amount_invalid");
        factory.StateOf(tooMuch).ShouldBe(EscrowState.Disputed, "a refused refund leaves the dispute open");
        factory.Disputes.RefundFor(tooMuch.Id).ShouldBeNull();

        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Disputes.RefundFor(exact.Id)!.Amount.Rupiah.ShouldBe(OrderTotal);
        factory.Audit.Entries.ShouldHaveSingleItem("only the accepted ruling is audited");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-4_274_000)]
    public async Task A_refund_of_nothing_or_less_is_refused(long amount)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund tidak masuk akal.", refundAmount = amount });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("dispute.refund_amount_invalid");
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refund_amount_that_is_not_a_whole_number_is_refused_at_the_boundary()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Raw(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            """{"resolution":"RefundToBuyer","reason":"Refund desimal.","refundAmount":1500.5}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    // ─── The remainder of a partial refund ───────────────────────────────────────
    // Refunding Rp1.000.000 of Rp4.274.000 leaves Rp3.274.000; at 5% a release pays the seller Rp3.110.300.

    [Fact]
    public async Task A_partial_refund_with_no_choice_holds_the_remainder_and_owes_the_seller_nothing()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund sebagian saja.", refundAmount = 1_000_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Disputes.RefundFor(order.Id)!.Disposition.ShouldBe(RemainderDisposition.Held);
        factory.Disputes.Payouts.ShouldBeEmpty();
        factory.Audit.Entries.ShouldHaveSingleItem().After.ShouldNotBeNull().ShouldContain("held for a later decision");

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");
        var refund = found.GetProperty("refund");
        refund.GetProperty("remainder").GetInt64().ShouldBe(3_274_000);
        refund.GetProperty("remainderStatus").GetString().ShouldBe("held");
        refund.GetProperty("sellerPayout").GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Releasing_the_remainder_with_the_ruling_writes_the_sellers_payout_less_the_fee_in_the_same_commit()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Sisa dirilis ke penjual.", refundAmount = 1_000_000, remainder = "release" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var payout = factory.Disputes.Payouts.ShouldHaveSingleItem();
        payout.OrderId.ShouldBe(order.Id);
        payout.SellerId.ShouldBe(Seller);
        payout.Amount.Rupiah.ShouldBe(3_110_300);
        payout.Fee.Rupiah.ShouldBe(163_700);
        factory.Disputes.RefundFor(order.Id)!.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        factory.Audit.Entries.ShouldHaveSingleItem().After.ShouldNotBeNull().ShouldContain("released to the seller");
    }

    [Fact]
    public async Task Splitting_the_remainder_pays_the_seller_the_typed_share_and_the_platform_keeps_the_rest()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Bagi dua sisanya.", refundAmount = 1_000_000, remainder = "split", sellerPayout = 1_200_000 });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var payout = factory.Disputes.Payouts.ShouldHaveSingleItem();
        payout.Amount.Rupiah.ShouldBe(1_200_000);
        payout.Fee.Rupiah.ShouldBe(2_074_000);
        factory.Disputes.RefundFor(order.Id)!.Disposition.ShouldBe(RemainderDisposition.Split);
    }

    [Theory]
    [InlineData("split", 0L)]
    [InlineData("split", 3_274_001L)]
    [InlineData("split", null)]
    public async Task A_split_amount_outside_the_remainder_is_refused_and_the_dispute_stays_open(string mode, long? share)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Bagi dua dengan jumlah salah.", refundAmount = 1_000_000, remainder = mode, sellerPayout = share });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("dispute.remainder_amount_invalid");
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
        factory.Disputes.RefundFor(order.Id).ShouldBeNull();
        factory.Disputes.Payouts.ShouldBeEmpty();
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("sell")]
    [InlineData("everything")]
    public async Task A_way_to_handle_the_remainder_the_page_does_not_know_is_refused(string mode)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Cara tidak dikenal.", refundAmount = 1_000_000, remainder = mode });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("dispute.remainder_invalid");
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
    }

    [Fact]
    public async Task A_choice_for_a_full_refund_or_a_ruling_for_the_seller_is_refused()
    {
        using var factory = new AdminApiFactory();
        var full = factory.Dispute();
        var forSeller = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var onFull = await Post(
            client, $"/api/admin/disputes/{full.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund penuh dengan pilihan.", refundAmount = OrderTotal, remainder = "release" });
        using var onSeller = await Post(
            client, $"/api/admin/disputes/{forSeller.Id}/resolve",
            new { resolution = "ReleaseToSeller", reason = "Putusan untuk penjual.", remainder = "release" });

        onFull.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(onFull)).ShouldBe("dispute.remainder_invalid");
        onSeller.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(onSeller)).ShouldBe("dispute.remainder_invalid");
        factory.StateOf(full).ShouldBe(EscrowState.Disputed);
        factory.StateOf(forSeller).ShouldBe(EscrowState.Disputed);
        factory.Disputes.Payouts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_held_remainder_can_be_released_later_with_one_payout_and_one_audit_entry()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();
        factory.Clock.UtcNow += TimeSpan.FromHours(2);

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode = "release", reason = "Penjual terbukti tidak bersalah." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var payout = factory.Disputes.Payouts.ShouldHaveSingleItem();
        payout.Amount.Rupiah.ShouldBe(3_110_300);
        var refund = factory.Disputes.RefundFor(order.Id)!;
        refund.Disposition.ShouldBe(RemainderDisposition.ReleasedToSeller);
        refund.SettledAt.ShouldBe(factory.Clock.UtcNow);

        factory.Audit.Entries.Count.ShouldBe(2);
        var entry = factory.Audit.Entries[^1];
        entry.Action.ShouldBe("dispute.remainder_settled");
        entry.TargetId.ShouldBe(order.Id);
        entry.Reason.ShouldBe("Penjual terbukti tidak bersalah.");
        entry.Before.ShouldNotBeNull().ShouldContain("held");

        var found = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes/{order.Id}");
        found.GetProperty("refund").GetProperty("remainderStatus").GetString().ShouldBe("releasedToSeller");
    }

    [Fact]
    public async Task A_held_remainder_can_be_split_later_and_a_second_settlement_is_refused()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();

        using var first = await Post(
            client, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode = "split", sellerPayout = 800_000, reason = "Bagi sebagian." });
        using var second = await Post(
            client, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode = "release", reason = "Mau rilis penuh saja." });

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(second)).ShouldBe("dispute.remainder_not_held");
        factory.Disputes.Payouts.ShouldHaveSingleItem().Amount.Rupiah.ShouldBe(800_000);
        factory.Disputes.RefundFor(order.Id)!.Disposition.ShouldBe(RemainderDisposition.Split);
    }

    [Fact]
    public async Task Settling_a_remainder_that_is_not_held_is_refused_whatever_the_reason()
    {
        using var factory = new AdminApiFactory();
        var stillDisputed = factory.Dispute();
        var fullRefund = factory.Dispute();
        var released = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{fullRefund.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Refund penuh saja.", refundAmount = OrderTotal })).Dispose();
        (await Post(client, $"/api/admin/disputes/{released.Id}/resolve",
            new { resolution = "ReleaseToSeller", reason = "Rilis ke penjual." })).Dispose();
        var before = factory.Audit.Entries.Count;

        foreach (var target in new[] { stillDisputed, fullRefund, released })
        {
            using var response = await Post(
                client, $"/api/admin/disputes/{target.Id}/settle-remainder", new { mode = "release", reason = "Mencoba menyelesaikan sisa." });

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await Code(response)).ShouldBe("dispute.remainder_not_held");
        }

        factory.Audit.Entries.Count.ShouldBe(before, "a refused settlement leaves no trace");
        factory.Disputes.Payouts.Select(p => p.OrderId).ShouldBe([released.Id], "only the ruling for the seller owes anything");
    }

    [Theory]
    [InlineData(null, "split", 500_000L, "dispute.remainder_invalid")]
    [InlineData("alasan yang cukup", null, null, "dispute.remainder_invalid")]
    [InlineData("alasan yang cukup", "hold", null, "dispute.remainder_invalid")]
    [InlineData("alasan yang cukup", "split", 0L, "dispute.remainder_amount_invalid")]
    [InlineData("alasan yang cukup", "split", 9_999_999L, "dispute.remainder_amount_invalid")]
    [InlineData("alasan yang cukup", "release", 5_000L, "dispute.remainder_invalid")]
    public async Task A_settlement_needs_a_reason_and_a_way_that_releases_or_splits(string? reason, string? mode, long? share, string expected)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode, sellerPayout = share, reason });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // The missing reason is reported before the way is looked at.
        (await Code(response)).ShouldBe(reason is null ? "admin.reason_invalid" : expected);
        factory.Disputes.RefundFor(order.Id)!.Disposition.ShouldBe(RemainderDisposition.Held);
        factory.Disputes.Payouts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Staff_who_are_a_party_to_the_order_cannot_settle_its_remainder()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using (var other = factory.CreateStaffClient())
        {
            (await Post(other, $"/api/admin/disputes/{order.Id}/resolve",
                new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();
        }

        using var party = factory.CreateStaffClient(Seller, "alice");
        using var response = await Post(
            party, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode = "release", reason = "Menyelesaikan sisa sendiri." });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Code(response)).ShouldBe("admin.cannot_act_on_self");
        factory.Disputes.Payouts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Repeating_a_settlement_with_the_same_key_changes_nothing_the_second_time()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();
        var body = new { mode = "release", reason = "Penjual terbukti tidak bersalah." };
        var key = Guid.NewGuid().ToString("N");

        using var first = await Post(client, $"/api/admin/disputes/{order.Id}/settle-remainder", body, key);
        using var second = await Post(client, $"/api/admin/disputes/{order.Id}/settle-remainder", body, key);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Disputes.Payouts.ShouldHaveSingleItem();
        factory.Audit.Entries.Count(e => e.Action == "dispute.remainder_settled").ShouldBe(1);
    }

    [Fact]
    public async Task A_settlement_that_loses_the_race_inside_the_store_is_reported_as_not_held()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        (await Post(client, $"/api/admin/disputes/{order.Id}/resolve",
            new { resolution = "RefundToBuyer", reason = "Tahan dulu sisanya.", refundAmount = 1_000_000 })).Dispose();

        // A second staff member decided first: the stored refund is no longer held when this commit lands.
        var stored = factory.Disputes.RefundFor(order.Id)!;
        stored.SettleRemainder(factory.Orders.Current(order.Id)!, new RemainderChoice(RemainderMode.Release), factory.Clock);
        await factory.Disputes.SaveRefundOutcomeAsync(stored, CancellationToken.None);

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/settle-remainder", new { mode = "release", reason = "Kalah balapan." });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(response)).ShouldBe("dispute.remainder_not_held");
        factory.Disputes.Payouts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ruling_for_the_buyer_must_name_an_amount_and_one_for_the_seller_must_not()
    {
        using var factory = new AdminApiFactory();
        var forBuyer = factory.Dispute();
        var forSeller = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var noAmount = await Post(client, $"/api/admin/disputes/{forBuyer.Id}/resolve", new { resolution = "RefundToBuyer", reason = "Tanpa jumlah refund." });
        using var withAmount = await Post(
            client, $"/api/admin/disputes/{forSeller.Id}/resolve",
            new { resolution = "ReleaseToSeller", reason = "Rilis dengan jumlah refund.", refundAmount = 1000 });

        noAmount.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(noAmount)).ShouldBe("dispute.refund_amount_invalid");
        withAmount.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(withAmount)).ShouldBe("dispute.refund_amount_invalid");
        factory.StateOf(forBuyer).ShouldBe(EscrowState.Disputed);
        factory.StateOf(forSeller).ShouldBe(EscrowState.Disputed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    [InlineData("      ")]
    public async Task A_ruling_needs_a_reason_of_at_least_five_characters(string reason)
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "RefundToBuyer", reason, refundAmount = 1000 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.reason_invalid");
        factory.StateOf(order).ShouldBe(EscrowState.Disputed);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reason_over_a_thousand_characters_is_refused_and_exactly_a_thousand_is_accepted()
    {
        using var factory = new AdminApiFactory();
        var tooLong = factory.Dispute();
        var longest = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var refused = await Post(
            client, $"/api/admin/disputes/{tooLong.Id}/resolve", new { resolution = "ReleaseToSeller", reason = new string('r', 1001) });
        using var accepted = await Post(
            client, $"/api/admin/disputes/{longest.Id}/resolve", new { resolution = "ReleaseToSeller", reason = new string('r', 1000) });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(refused)).ShouldBe("admin.reason_invalid");
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_refund_the_gateway_cannot_take_still_stands_as_recorded()
    {
        using var factory = new AdminApiFactory();
        factory.Refunds.Fail = true;
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "RefundToBuyer", reason = "Gateway sedang mati.", refundAmount = 1_000_000 });

        // The ruling is a decision already committed with its audit entry; a sender failing afterwards cannot undo it.
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.StateOf(order).ShouldBe(EscrowState.Refunded);
        factory.Disputes.RefundFor(order.Id)!.Status.ShouldBe(RefundStatus.Recorded);
        factory.Audit.Entries.ShouldHaveSingleItem();
    }

    // ─── Ruling for the seller: release ──────────────────────────────────────────

    [Fact]
    public async Task Ruling_for_the_seller_releases_the_order_and_owes_the_seller_exactly_once()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Foto sertifikat cocok dengan slab." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.StateOf(order).ShouldBe(EscrowState.Released);
        var payout = factory.Disputes.Payouts.ShouldHaveSingleItem();
        payout.OrderId.ShouldBe(order.Id);
        payout.SellerId.ShouldBe(Seller);
        payout.Amount.ShouldBe(order.SellerPayout);
        factory.Disputes.RefundFor(order.Id).ShouldBeNull("a ruling for the seller refunds nobody");
        factory.Refunds.Calls.ShouldBe(0);

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("dispute.resolved_for_seller");
        entry.Reason.ShouldBe("Foto sertifikat cocok dengan slab.");
        factory.Disputes.Notices.ShouldAllBe(n => n.Kind == OrderNoticeKind.DisputeResolvedForSeller);
        factory.Disputes.Notices.Count.ShouldBe(2);

        var resolved = await client.GetFromJsonAsync<JsonElement>("/api/admin/disputes?status=resolved");
        resolved.EnumerateArray().Select(e => e.GetProperty("orderId").GetString()).ToList().ShouldBe([order.Id]);
        resolved[0].GetProperty("resolution").GetString().ShouldBe("ReleaseToSeller");
        (await Ids(client, "q=" + order.OrderNumber)).ShouldBeEmpty("a resolved dispute leaves the open queue");
    }

    // ─── Ruling twice ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_second_ruling_by_another_member_of_staff_is_refused_and_changes_nothing()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var alice = factory.CreateStaffClient(AdminApiFactory.StaffAlice, "alice");
        using var bob = factory.CreateStaffClient(AdminApiFactory.StaffBob, "bob");

        using var first = await Post(alice, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "RefundToBuyer", reason = "Alice memutuskan refund.", refundAmount = 500_000 });
        using var second = await Post(bob, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Bob memutuskan rilis." });

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(second)).ShouldBe("dispute.already_resolved");
        factory.StateOf(order).ShouldBe(EscrowState.Refunded);
        factory.Disputes.Payouts.ShouldBeEmpty();
        factory.Refunds.Calls.ShouldBe(1);
        factory.Audit.Entries.ShouldHaveSingleItem().ActorName.ShouldBe("alice");
    }

    [Fact]
    public async Task A_ruling_that_loses_the_race_inside_the_store_is_reported_as_already_resolved()
    {
        // Two staff read the dispute as open and both try to commit: the store lets one through and tells the other the order changed
        // underneath it. The loser gets the same answer a late arrival gets, and nothing of theirs is written.
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        factory.Disputes.ConflictOnNextResolve = true;
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Kalah balapan." });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(response)).ShouldBe("dispute.already_resolved");
        factory.Disputes.Payouts.ShouldBeEmpty();
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_write_the_table_cannot_take_for_another_reason_is_a_retryable_503_and_the_dispute_stays_open()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        factory.Disputes.SpuriousConflictOnNextResolve = true;
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();
        var body = new { resolution = "ReleaseToSeller", reason = "Tabel sedang sibuk." };

        using var busy = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body, key);
        using var retried = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body, key);

        busy.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Code(busy)).ShouldBe("concurrency.conflict");
        retried.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.StateOf(order).ShouldBe(EscrowState.Released);
        factory.Disputes.Payouts.ShouldHaveSingleItem();
        factory.Audit.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sending_the_same_ruling_twice_with_one_idempotency_key_changes_things_once()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();
        var body = new { resolution = "RefundToBuyer", reason = "Refund sekali saja.", refundAmount = 750_000 };

        using var first = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body, key);
        using var second = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body, key);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Audit.Entries.ShouldHaveSingleItem();
        factory.Disputes.Notices.Count.ShouldBe(2);
        factory.Refunds.Calls.ShouldBe(1);
        factory.Disputes.RefundFor(order.Id)!.Amount.Rupiah.ShouldBe(750_000);
    }

    [Fact]
    public async Task Without_an_idempotency_key_a_repeated_ruling_is_refused_not_applied_again()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        var body = new { resolution = "ReleaseToSeller", reason = "Rilis tanpa kunci." };

        using var first = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body);
        using var second = await Post(client, $"/api/admin/disputes/{order.Id}/resolve", body);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        factory.Disputes.Payouts.Count.ShouldBe(1);
        factory.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_audit_write_fails_the_ruling_commits_nothing()
    {
        using var factory = new AdminApiFactory();
        var order = factory.Dispute();
        using var client = factory.CreateStaffClient();
        factory.Audit.FailNextAppend = true;

        using var response = await Post(
            client, $"/api/admin/disputes/{order.Id}/resolve", new { resolution = "ReleaseToSeller", reason = "Audit sedang gagal." });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await Code(response)).ShouldBe("server.error");
        factory.Disputes.Payouts.ShouldBeEmpty();
        factory.Disputes.Notices.ShouldBeEmpty();
        factory.Disputes.Notes.ShouldBeEmpty();
        factory.Audit.Entries.ShouldBeEmpty();
        factory.Refunds.Calls.ShouldBe(0, "nothing is sent for a ruling that was not committed");
    }

    // ─── Holidays ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_holiday_list_defaults_to_the_current_year_in_jakarta_and_can_be_asked_for_another()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        var thisYear = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays");
        var explicitYear = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays?year=2026");
        var otherYear = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays?year=2028");

        thisYear.GetArrayLength().ShouldBe(HolidaySeed.Year2026.Count);
        explicitYear.GetArrayLength().ShouldBe(HolidaySeed.Year2026.Count);
        otherYear.GetArrayLength().ShouldBe(0);
        explicitYear[0].EnumerateObject().Select(p => p.Name).ToList().ShouldBe(["date", "name", "kind"]);
        explicitYear.EnumerateArray().Select(e => e.GetProperty("date").GetString()).ToList().ShouldBe(
            [.. HolidaySeed.Year2026.Select(h => h.Date).Order(StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task The_next_year_is_seeded_too_and_read_back_in_date_order()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        var next = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays?year=2027");

        next.EnumerateArray().Select(e => e.GetProperty("date").GetString()).ToList().ShouldBe(
            [.. HolidaySeed.Year2027.Select(h => h.Date).Order(StringComparer.Ordinal)]);
    }

    [Theory]
    [InlineData("year=1999")]
    [InlineData("year=2101")]
    public async Task A_year_outside_the_accepted_range_is_a_coded_400(string query)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/holidays?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.holiday_invalid");
    }

    [Fact]
    public async Task Adding_a_holiday_lists_it_and_writes_one_audit_entry()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, "/api/admin/holidays", new { date = "2026-12-31", name = "Cuti bersama contoh", kind = "collectiveLeave" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays?year=2026");
        var added = listed.EnumerateArray().Single(e => e.GetProperty("date").GetString() == "2026-12-31");
        added.GetProperty("name").GetString().ShouldBe("Cuti bersama contoh");
        added.GetProperty("kind").GetString().ShouldBe("collectiveLeave");

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("holiday.added");
        entry.TargetType.ShouldBe("holiday");
        entry.TargetId.ShouldBe("2026-12-31");
        entry.ActorName.ShouldBe("alice");
    }

    [Fact]
    public async Task Adding_a_date_that_is_already_listed_is_a_409_with_its_code_and_audits_nothing()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var existing = HolidaySeed.Year2026[0].Date;

        using var response = await Post(client, "/api/admin/holidays", new { date = existing, name = "Dobel", kind = "national" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Code(response)).ShouldBe("holiday.exists");
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"date":"2026-02-30","name":"Tanggal fiktif","kind":"national"}""")]
    [InlineData("""{"date":"31-12-2026","name":"Format salah","kind":"national"}""")]
    [InlineData("""{"date":"2026-12-31","name":"","kind":"national"}""")]
    [InlineData("""{"date":"2026-12-31","name":"Jenis salah","kind":"festival"}""")]
    [InlineData("""{"date":"1999-12-31","name":"Terlalu lama","kind":"national"}""")]
    [InlineData("""{}""")]
    public async Task A_bad_holiday_is_a_coded_400_and_audits_nothing(string body)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await Raw(client, "/api/admin/holidays", body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(response)).ShouldBe("admin.holiday_invalid");
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Adding_the_same_holiday_twice_with_one_idempotency_key_audits_it_once()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();
        var body = new { date = "2026-12-30", name = "Sekali saja", kind = "national" };

        using var first = await Post(client, "/api/admin/holidays", body, key);
        using var second = await Post(client, "/api/admin/holidays", body, key);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Audit.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Removing_a_holiday_drops_it_from_the_list_and_audits_what_was_removed()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var target = HolidaySeed.Year2026[0];

        using var response = await Post(client, $"/api/admin/holidays/{target.Date}/remove", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/admin/holidays?year=2026");
        listed.EnumerateArray().Select(e => e.GetProperty("date").GetString()).ToList().ShouldNotContain(target.Date);

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("holiday.removed");
        entry.TargetId.ShouldBe(target.Date);
        entry.Before.ShouldNotBeNull().ShouldContain(target.Name);
    }

    [Theory]
    [InlineData("2026-12-31")]
    [InlineData("not-a-date")]
    [InlineData("2026-02-30")]
    public async Task Removing_a_date_that_is_not_listed_is_a_404_with_its_code(string date)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await Post(client, $"/api/admin/holidays/{date}/remove", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Code(response)).ShouldBe("holiday.not_found");
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_the_same_holiday_twice_with_one_idempotency_key_audits_it_once()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var date = HolidaySeed.Year2026[1].Date;
        var key = Guid.NewGuid().ToString();

        using var first = await Post(client, $"/api/admin/holidays/{date}/remove", new { }, key);
        using var second = await Post(client, $"/api/admin/holidays/{date}/remove", new { }, key);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Audit.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task When_the_audit_write_fails_the_holiday_change_is_not_made()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var before = factory.Holidays.Count;
        factory.Audit.FailNextAppend = true;

        using var response = await Post(client, "/api/admin/holidays", new { date = "2026-12-29", name = "Gagal", kind = "national" });

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        factory.Holidays.Count.ShouldBe(before);
    }

    // ─── The first start seeds each known year ───────────────────────────────────

    [Fact]
    public async Task The_first_start_against_an_empty_table_seeds_each_known_year_once_and_audits_it_as_the_system()
    {
        using var parent = new AdminApiFactory();
        var empty = new FakeHolidayStore(parent.Audit);
        using var factory = parent.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<Koleksi.Application.Abstractions.IHolidayStore>(empty)));

        using var client = factory.CreateClient();
        using var first = await client.GetAsync("/health");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        empty.Count.ShouldBe(HolidaySeed.ByYear.Values.Sum(seed => seed.Count));
        parent.Audit.Entries.Count.ShouldBe(HolidaySeed.ByYear.Count, "one entry per year");
        parent.Audit.Entries.ShouldAllBe(entry => entry.Action == "holiday.seeded" && entry.ActorId == "system" && entry.TargetType == "holiday");
        parent.Audit.Entries.Select(entry => entry.TargetId).Order(StringComparer.Ordinal).ToList().ShouldBe(["2026", "2027"]);
    }

    [Fact]
    public async Task A_second_start_does_not_put_back_a_holiday_staff_removed()
    {
        using var parent = new AdminApiFactory();
        var store = new FakeHolidayStore(parent.Audit);
        using var firstHost = parent.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<Koleksi.Application.Abstractions.IHolidayStore>(store)));
        using (var client = firstHost.CreateClient())
        {
            (await client.GetAsync("/health")).Dispose();
        }

        var removed = HolidaySeed.Year2026[2].Date;
        var removal = AuditEntry.Create(parent.Clock, "staff", "alice", AuditActions.HolidayRemoved, AuditTargets.Holiday, removed, "r-1");
        (await store.RemoveAsync(removed, removal, CancellationToken.None)).ShouldBe(Koleksi.Application.Abstractions.HolidayRemoval.Removed);

        using var secondHost = parent.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
            services.AddSingleton<Koleksi.Application.Abstractions.IHolidayStore>(store)));
        using (var client = secondHost.CreateClient())
        {
            (await client.GetAsync("/health")).Dispose();
        }

        (await store.FindAsync(removed, CancellationToken.None)).ShouldBeNull();
        parent.Audit.Entries.Count(e => e.Action == "holiday.seeded").ShouldBe(HolidaySeed.ByYear.Count, "each year once, however often the host starts");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Raw(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    private static async Task<string?> Code(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private static async Task<List<string?>> Ids(HttpClient client, string query)
    {
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/admin/disputes?status=open&{query}");
        return [.. list.EnumerateArray().Select(e => e.GetProperty("orderId").GetString())];
    }
}

/// <summary>Builds the records these tests read, in the factory's in-memory stores.</summary>
internal static class BackofficeScenarios
{
    private const string Buyer = "buyer-sub-0001";
    private const string Seller = "seller-sub-0002";

    /// <summary>The state of the order as stored, after whatever the request committed.</summary>
    public static EscrowState StateOf(this AdminApiFactory factory, Order order) => factory.Orders.Current(order.Id)!.EscrowState;

    public static void Person(this AdminApiFactory factory, string userId, string handle) =>
        factory.Profiles.Add(PublicProfile.Create(userId, handle, factory.Clock.UtcNow.AddYears(-1), factory.Clock));

    /// <summary>A paid, shipped and delivered order, not yet disputed. Items Rp4.250.000 plus shipping Rp24.000.</summary>
    public static Order Delivered(this AdminApiFactory factory, string buyer = Buyer, string seller = Seller)
    {
        var clock = factory.Clock;
        var order = Order.Create(
            buyer, seller,
            [new OrderLine("L-" + Guid.NewGuid().ToString("N")[..8], "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet Barat", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
            clock);
        order.ConfirmPayment(order.GrandTotal, clock);
        order.MarkShipped(Shipment.Create("jne", "JNE0012345678", "p.jpg", "l.jpg", new DateOnly(2026, 1, 1)), seller, clock);
        order.MarkDelivered(clock);
        factory.Orders.Add(order);
        return order;
    }

    /// <summary>A delivered order whose buyer opened a dispute at <paramref name="openedAt"/> (now when not given).</summary>
    public static Order Dispute(this AdminApiFactory factory, string buyer = Buyer, string seller = Seller, DateTimeOffset? openedAt = null)
    {
        var order = factory.Delivered(buyer, seller);
        var real = factory.Clock.UtcNow;
        if (openedAt is { } at)
        {
            factory.Clock.UtcNow = at;
        }

        order.OpenDispute("Kartu yang datang bukan PSA 10 seperti di deskripsi.", buyer, factory.Clock);
        factory.Clock.UtcNow = real;
        return order;
    }

    /// <summary>One chat message in the thread about the order's first listing between its buyer and seller.</summary>
    public static Task Say(this AdminApiFactory factory, Order order, string sender, string text) =>
        factory.SayAbout(order.Lines[0].ListingId, order.BuyerId, order.SellerId, sender, text);

    public static async Task SayAbout(this AdminApiFactory factory, string listingId, string buyer, string seller, string sender, string text)
    {
        var id = Conversation.IdFor(listingId, buyer);
        var conversation = await factory.Conversations.FindAsync(id, CancellationToken.None)
            ?? Conversation.Start(listingId, "Judul", null, buyer, "pembeli", seller, "penjual", factory.Clock);

        factory.Clock.UtcNow = factory.Clock.UtcNow.AddSeconds(1);
        var message = ChatMessage.Create(conversation.Id, sender, text, factory.Clock);
        conversation.RecordMessage(message);
        await factory.Conversations.AppendAsync(conversation, message, CancellationToken.None);
    }
}
