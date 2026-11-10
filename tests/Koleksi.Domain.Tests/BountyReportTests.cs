using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class BountyReportTests
{
    private const string Buyer = "buyer-sub";
    private const string Reporter = "reporter-sub";
    private static readonly IReadOnlyDictionary<string, string> NoFacets = new Dictionary<string, string>();

    private static Bounty Open(TestClock clock) => Bounty.Create(
        Buyer, "kolektor", "PSA 10 Charizard Base Set", "cards", NoFacets, "Cari yang mulus.", ["PSA 10"],
        Money.FromRupiah(1_000_000), "Bandung", null, 14, clock);

    private static string CodeOf(Action act) => Should.Throw<DomainException>(act).Code;

    [Fact]
    public void A_report_carries_the_reason_a_trimmed_note_and_the_time()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);

        var report = BountyReport.Create(bounty, Reporter, BountyReportReason.Scam, "  minta transfer luar  ", clock);

        report.BountyId.ShouldBe(bounty.Id);
        report.ReporterId.ShouldBe(Reporter);
        report.Reason.ShouldBe(BountyReportReason.Scam);
        report.Note.ShouldBe("minta transfer luar");
        report.CreatedAt.ShouldBe(clock.UtcNow);
    }

    [Fact]
    public void A_blank_note_is_no_note()
    {
        var clock = TestClock.AtEpoch();

        BountyReport.Create(Open(clock), Reporter, BountyReportReason.Spam, "   ", clock).Note.ShouldBeNull();
    }

    [Fact]
    public void The_buyer_cannot_report_their_own_dicari()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyReport.Create(Open(clock), Buyer, BountyReportReason.Spam, null, clock)).ShouldBe("bounty.report_own");
    }

    [Fact]
    public void A_dicari_that_is_no_longer_open_cannot_be_reported()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);
        bounty.Remove("Melanggar aturan.");

        CodeOf(() => BountyReport.Create(bounty, Reporter, BountyReportReason.Spam, null, clock)).ShouldBe("bounty.not_open");
    }

    [Fact]
    public void An_undefined_reason_is_refused()
    {
        var clock = TestClock.AtEpoch();

        CodeOf(() => BountyReport.Create(Open(clock), Reporter, (BountyReportReason)99, null, clock)).ShouldBe("bounty.report_invalid");
    }

    [Fact]
    public void A_note_over_the_limit_or_with_control_characters_is_refused()
    {
        var clock = TestClock.AtEpoch();
        var bounty = Open(clock);

        CodeOf(() => BountyReport.Create(bounty, Reporter, BountyReportReason.Other, new string('a', BountyReport.MaxNoteLength + 1), clock))
            .ShouldBe("bounty.report_invalid");
        CodeOf(() => BountyReport.Create(bounty, Reporter, BountyReportReason.Other, "a\u0007b", clock)).ShouldBe("bounty.report_invalid");
    }
}
