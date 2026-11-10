using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Reporting a Dicari: who may, what comes back, and what is stored.</summary>
public sealed class BountyReportTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly string[] PsaCondition = ["PSA 10"];

    [Fact]
    public async Task Reporting_needs_a_signed_in_user()
    {
        var bountyId = await PostAsync();
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsJsonAsync($"/api/bounties/{bountyId}/report", new { reason = "Spam" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_report_is_stored_for_staff_and_a_repeat_from_the_same_person_answers_the_same()
    {
        var bountyId = await PostAsync();
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "pelapor.satu");

        using var first = await client.PostAsJsonAsync($"/api/bounties/{bountyId}/report", new { reason = "Scam", note = "Minta transfer di luar." });
        using var again = await client.PostAsJsonAsync($"/api/bounties/{bountyId}/report", new { reason = "Spam" });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ReadJsonAsync()).GetProperty("reported").GetBoolean().ShouldBeTrue();
        var stored = factory.BountyReports.All.Where(r => r.BountyId == bountyId).ToList();
        stored.Count.ShouldBe(1);
        stored[0].Reason.ShouldBe(BountyReportReason.Scam);
    }

    [Fact]
    public async Task The_owner_cannot_report_their_own_dicari()
    {
        var (owner, bountyId) = await PostWithOwnerAsync();
        using var client = factory.CreateClientAs(owner, "pemilik");

        using var response = await client.PostAsJsonAsync($"/api/bounties/{bountyId}/report", new { reason = "Spam" });

        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.report_own");
        factory.BountyReports.All.ShouldNotContain(r => r.BountyId == bountyId);
    }

    [Fact]
    public async Task A_missing_dicari_is_a_404()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "pelapor.dua");

        using var response = await client.PostAsJsonAsync($"/api/bounties/{Guid.NewGuid()}/report", new { reason = "Spam" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("")]
    [InlineData(null)]
    public async Task An_unknown_reason_is_refused(string? reason)
    {
        var bountyId = await PostAsync();
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), "pelapor.tiga");

        using var response = await client.PostAsJsonAsync($"/api/bounties/{bountyId}/report", new { reason });

        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("bounty.report_invalid");
    }

    private async Task<string> PostAsync() => (await PostWithOwnerAsync()).BountyId;

    private async Task<(string Owner, string BountyId)> PostWithOwnerAsync()
    {
        var owner = Guid.NewGuid().ToString();
        var profile = UserProfile.CreateEmpty(owner, factory.Clock);
        profile.UpdateDetails(
            "Pembeli Uji",
            [new Address("alamat-1", "Rumah", "Pembeli Uji", "081234567890", "Jl. Merdeka 1", "Bandung", "Jawa Barat", "40111", true)],
            factory.Clock);
        await factory.Profiles.AddAsync(profile, CancellationToken.None);

        using var client = factory.CreateClientAs(owner, "pemilik");
        using var response = await client.PostAsJsonAsync("/api/bounties", new
        {
            title = "Dicari PSA 10 Charizard Base Set 1999",
            categoryId = TestData.CardsCategoryId,
            facets = new Dictionary<string, string>(),
            description = "Mencari kartu dalam kondisi mulus.",
            conditions = PsaCondition,
            maxBudgetRupiah = 2_000_000,
            photoKeys = Array.Empty<string>(),
            durationDays = 14,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (owner, (await response.ReadJsonAsync()).GetProperty("bountyId").GetString()!);
    }
}
