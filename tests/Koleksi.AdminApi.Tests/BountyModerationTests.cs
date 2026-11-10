using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>Staff handling of reported Dicari: the queue, removal with its audit entry, and dismissal.</summary>
public sealed class BountyModerationTests
{
    private static readonly string[] Conditions = ["PSA 10"];

    [Fact]
    public async Task The_queue_lists_a_reported_dicari_once_with_its_reasons()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        Report(factory, bounty, "r1", BountyReportReason.Spam);
        Report(factory, bounty, "r2", BountyReportReason.Scam);
        using var client = factory.CreateStaffClient();

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/admin/bounties/reported");

        rows.GetArrayLength().ShouldBe(1);
        rows[0].GetProperty("bountyId").GetString().ShouldBe(bounty.Id);
        rows[0].GetProperty("reportCount").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task The_detail_shows_the_reports_without_naming_who_made_them()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        Report(factory, bounty, "reporter-secret-id", BountyReportReason.ContactInfo);
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/bounties/{bounty.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("reporter-secret-id");
    }

    [Fact]
    public async Task Removing_a_dicari_removes_it_settles_its_reports_and_writes_one_audit_entry_with_the_reason()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        Report(factory, bounty, "r1", BountyReportReason.Prohibited);
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{bounty.Id}/remove", new { reason = "Barang terlarang." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await factory.Bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Removed);
        factory.BountyReports.All.ShouldBeEmpty();
        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe(AuditActions.BountyRemoved);
        entry.TargetId.ShouldBe(bounty.Id);
        entry.Reason.ShouldBe("Barang terlarang.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public async Task Removal_without_a_real_reason_is_refused_and_changes_nothing(string reason)
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{bounty.Id}/remove", new { reason });

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("bounty.removal_reason_invalid");
        (await factory.Bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Open);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removal_is_refused_while_a_deal_is_in_flight()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory, acceptedProposalId: "proposal-1");
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{bounty.Id}/remove", new { reason = "Barang terlarang." });

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("bounty.deal_in_progress");
        (await factory.Bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Open);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_an_unknown_dicari_is_a_404()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{EntityId.New(DateTimeOffset.UtcNow)}/remove", new { reason = "Barang terlarang." });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dismissing_clears_the_reports_leaves_the_dicari_up_and_is_audited()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        Report(factory, bounty, "r1", BountyReportReason.Spam);
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{bounty.Id}/dismiss", new { note = "Tidak melanggar." });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.BountyReports.All.ShouldBeEmpty();
        (await factory.Bounties.FindAsync(bounty.Id, CancellationToken.None))!.Status.ShouldBe(BountyStatus.Open);
        factory.Audit.Entries.ShouldHaveSingleItem().Action.ShouldBe(AuditActions.BountyReportsDismissed);
    }

    [Fact]
    public async Task Dismissing_a_dicari_with_no_reports_is_refused()
    {
        using var factory = new AdminApiFactory();
        var bounty = Seed(factory);
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsync($"/api/admin/bounties/{bounty.Id}/dismiss", new { });

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("bounty.no_reports");
    }

    private static Bounty Seed(AdminApiFactory factory, string? acceptedProposalId = null)
    {
        var now = factory.Clock.UtcNow;
        var bounty = Bounty.Rehydrate(
            EntityId.New(now), "buyer-1", "pembeli", "Dicari PSA 10 Charizard", "cat-cards", new Dictionary<string, string>(),
            "Mencari kartu mulus.", Conditions, Money.FromRupiah(2_000_000), "Bandung", [], now, now.AddDays(14),
            BountyStatus.Open, 0, acceptedProposalId, null, acceptedProposalId is null ? null : now.AddDays(2), 1);
        factory.Bounties.Seed(bounty);
        return bounty;
    }

    private static void Report(AdminApiFactory factory, Bounty bounty, string reporter, BountyReportReason reason) =>
        factory.BountyReports.Seed(BountyReport.Rehydrate(bounty.Id, reporter, reason, null, factory.Clock.UtcNow));
}
