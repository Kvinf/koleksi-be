using Koleksi.Domain.Common;

namespace Koleksi.Domain.Bounties;

/// <summary>Why someone reported a Dicari.</summary>
public enum BountyReportReason
{
    Spam = 0,

    /// <summary>The item is not allowed on the marketplace.</summary>
    Prohibited = 1,

    Scam = 2,

    /// <summary>It tries to take the deal off the platform (phone numbers, links).</summary>
    ContactInfo = 3,

    Other = 4,
}

/// <summary>
/// One person's report of a Dicari, for staff to look at. A person reports a Dicari once, and cannot report their own.
/// </summary>
/// <remarks>
/// The reporter's note is for staff only; it is never sent to the buyer who posted the Dicari.
/// </remarks>
public sealed class BountyReport
{
    public const int MaxNoteLength = 300;

    /// <summary>The most reports kept for one Dicari. More add nothing staff need, and it bounds the work of settling them.</summary>
    public const int MaxPerBounty = 25;

    private BountyReport(string bountyId, string reporterId, BountyReportReason reason, string? note, DateTimeOffset createdAt)
    {
        BountyId = bountyId;
        ReporterId = reporterId;
        Reason = reason;
        Note = note;
        CreatedAt = createdAt;
    }

    public string BountyId { get; }

    public string ReporterId { get; }

    public BountyReportReason Reason { get; }

    public string? Note { get; }

    public DateTimeOffset CreatedAt { get; }

    public static BountyReport Create(Bounty bounty, string reporterId, BountyReportReason reason, string? note, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(bounty);
        ArgumentException.ThrowIfNullOrWhiteSpace(reporterId);
        ArgumentNullException.ThrowIfNull(clock);

        if (!Enum.IsDefined(reason))
        {
            throw DomainException.For("bounty.report_invalid", "Choose why you are reporting this Dicari.");
        }

        if (string.Equals(bounty.BuyerId, reporterId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.report_own", "You cannot report your own Dicari.");
        }

        if (bounty.Status != BountyStatus.Open)
        {
            throw DomainException.For("bounty.not_open", "This Dicari is no longer active.");
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is { Length: > MaxNoteLength } || (trimmed is not null && trimmed.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            throw DomainException.For("bounty.report_invalid", "Keep the note under 300 characters.");
        }

        return new BountyReport(bounty.Id, reporterId, reason, trimmed, clock.UtcNow);
    }

    /// <summary>Rebuilds a stored report. No rules: it was valid when it was written.</summary>
    public static BountyReport Rehydrate(string bountyId, string reporterId, BountyReportReason reason, string? note, DateTimeOffset createdAt) =>
        new(bountyId, reporterId, reason, note, createdAt);
}
