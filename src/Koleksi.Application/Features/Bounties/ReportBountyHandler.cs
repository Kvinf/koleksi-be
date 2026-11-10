using Koleksi.Application.Abstractions;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Bounties;

/// <summary>
/// Someone reports a Dicari for staff to look at. Returns <c>false</c> when the Dicari does not exist (the same answer a
/// stranger gets for any missing id).
/// </summary>
/// <remarks>
/// A repeat report from the same person, or one past the cap on a Dicari, is accepted and quietly ignored: the answer must not
/// tell a reporter what staff already hold, and a Dicari that has enough reports to look at needs no more.
/// </remarks>
public sealed class ReportBountyHandler(
    IBountyRepository bounties,
    IBountyModerationStore moderation,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<bool> HandleAsync(string bountyId, string? reason, string? note, CancellationToken ct)
    {
        var reporterId = currentUser.RequireUserId();

        var bounty = await bounties.FindAsync(bountyId, ct).ConfigureAwait(false);
        if (bounty is null || bounty.Status == BountyStatus.Removed)
        {
            return false;
        }

        if (!Enum.TryParse<BountyReportReason>(reason, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw DomainException.For("bounty.report_invalid", "Choose why you are reporting this Dicari.");
        }

        var report = BountyReport.Create(bounty, reporterId, parsed, note, clock);
        await moderation.AddReportAsync(report, ct).ConfigureAwait(false);
        return true;
    }
}
