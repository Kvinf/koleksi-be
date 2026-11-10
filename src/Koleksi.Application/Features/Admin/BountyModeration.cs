using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Bounties;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Admin;

/// <summary>One Dicari with unsettled reports, as the staff queue shows it.</summary>
public sealed record ReportedBountyRow(
    string BountyId,
    string Code,
    string Title,
    string CategoryId,
    string BuyerUsername,
    string Status,
    int ReportCount,
    IReadOnlyList<string> Reasons,
    DateTimeOffset LatestReportAt);

/// <summary>One report as staff read it. The reporter is not named: staff judge the Dicari, not the reporter.</summary>
public sealed record BountyReportView(string Reason, string? Note, DateTimeOffset CreatedAt);

/// <summary>A reported Dicari in full, for the decision.</summary>
public sealed record ReportedBountyDetail(
    ReportedBountyRow Summary,
    string Description,
    IReadOnlyList<string> Conditions,
    long MaxBudgetRupiah,
    string ShipToCity,
    int ProposalCount,
    bool DealInFlight,
    string? RemovedReason,
    IReadOnlyList<BountyReportView> Reports);

public sealed record RemoveBountyRequest(string? Reason);

public sealed record DismissBountyReportsRequest(string? Note);

internal static class BountyModerationWire
{
    public static ReportedBountyRow Row(Bounty bounty, IReadOnlyList<BountyReport> reports) =>
        new(
            bounty.Id,
            bounty.Code,
            bounty.Title,
            bounty.CategoryId,
            bounty.BuyerUsername ?? "-",
            bounty.Status.ToString(),
            reports.Count,
            [.. reports.Select(r => r.Reason.ToString()).Distinct(StringComparer.Ordinal)],
            reports.Count == 0 ? bounty.CreatedAt : reports.Max(r => r.CreatedAt));
}

/// <summary>The reported Dicari, most recently reported first. One queue read, then one read per Dicari shown.</summary>
public sealed class ListReportedBountiesHandler(IBountyModerationStore moderation, IBountyRepository bounties)
{
    private const int QueueLimit = 100;

    public async Task<IReadOnlyList<ReportedBountyRow>> HandleAsync(CancellationToken ct)
    {
        var queue = await moderation.ListQueueAsync(QueueLimit, ct).ConfigureAwait(false);
        var rows = new List<ReportedBountyRow>();

        foreach (var group in queue.GroupBy(r => r.BountyId, StringComparer.Ordinal))
        {
            var bounty = await bounties.FindAsync(group.Key, ct).ConfigureAwait(false);
            if (bounty is not null)
            {
                rows.Add(BountyModerationWire.Row(bounty, [.. group]));
            }
        }

        return [.. rows.OrderByDescending(r => r.LatestReportAt)];
    }
}

/// <summary>One reported Dicari with its reports. 404 <c>bounty.not_found</c> when it does not exist.</summary>
public sealed class GetReportedBountyHandler(IBountyModerationStore moderation, IBountyRepository bounties)
{
    public async Task<ReportedBountyDetail> HandleAsync(string bountyId, CancellationToken ct)
    {
        var bounty = await FindAsync(bounties, bountyId, ct).ConfigureAwait(false);
        var reports = await moderation.ListReportsAsync(bounty.Id, ct).ConfigureAwait(false);

        return new ReportedBountyDetail(
            BountyModerationWire.Row(bounty, reports),
            bounty.Description,
            [.. bounty.Conditions],
            bounty.MaxBudget.Rupiah,
            bounty.ShipToCity,
            await ProposalCountAsync(bounties, bounty.Id, ct).ConfigureAwait(false),
            bounty.DealInFlight,
            bounty.RemovedReason,
            [.. reports.Select(r => new BountyReportView(r.Reason.ToString(), r.Note, r.CreatedAt))]);
    }

    internal static async Task<Bounty> FindAsync(IBountyRepository bounties, string bountyId, CancellationToken ct)
    {
        // The id is caller input: only a well-formed one reaches a key.
        if (!EntityId.IsValid(bountyId))
        {
            throw NotFound();
        }

        return await bounties.FindAsync(bountyId, ct).ConfigureAwait(false) ?? throw NotFound();
    }

    private static async Task<int> ProposalCountAsync(IBountyRepository bounties, string bountyId, CancellationToken ct) =>
        (await bounties.ListProposalsAsync(bountyId, ct).ConfigureAwait(false)).Count;

    internal static DomainException NotFound() => DomainException.For("bounty.not_found", "That Dicari does not exist.");
}

/// <summary>
/// Takes a Dicari down. The Dicari's new state, the reports it settles and the audit entry are one transaction; the
/// proposals still waiting on it lapse afterwards, best effort, since a Dicari that is no longer open takes no more of
/// them either way.
/// </summary>
/// <remarks>
/// Refused while a deal is in flight (<c>bounty.deal_in_progress</c>): a buyer is paying a seller right now, and pulling the
/// Dicari out from under that would strand an accepted offer. Staff try again once the deal settles. A fulfilled Dicari is
/// refused by the domain.
/// </remarks>
public sealed class RemoveBountyHandler(
    ICurrentStaff staff,
    IBountyRepository bounties,
    IBountyModerationStore moderation,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private const int MinReasonLength = 5;
    private const int MaxReasonLength = 500;
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string bountyId, RemoveBountyRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"bounty-remove:{staff.StaffId}:{bountyId}",
            idempotencyKey,
            Retention,
            () => RemoveAsync(bountyId, request, ct),
            ct,
            InFlightPolicy.Reject);
    }

    private async Task<ChangeDone> RemoveAsync(string bountyId, RemoveBountyRequest request, CancellationToken ct)
    {
        var reason = (request.Reason ?? string.Empty).Trim();
        if (reason.Length is < MinReasonLength or > MaxReasonLength || reason.Any(char.IsControl))
        {
            throw DomainException.For("bounty.removal_reason_invalid", "Give a reason of 5 to 500 characters.");
        }

        var bounty = await GetReportedBountyHandler.FindAsync(bounties, bountyId, ct).ConfigureAwait(false);
        if (bounty.Status == BountyStatus.Removed)
        {
            return new ChangeDone();
        }

        if (bounty.DealInFlight)
        {
            throw DomainException.For("bounty.deal_in_progress", "A buyer is paying for a proposal on this Dicari right now. Try again once it settles.");
        }

        var before = bounty.Status.ToString();
        var expectedVersion = bounty.Version;
        bounty.Remove(reason);

        var reports = await moderation.ListReportsAsync(bounty.Id, ct).ConfigureAwait(false);
        var audit = AuditEntry.Create(
            clock, staff.StaffId, staff.StaffName, AuditActions.BountyRemoved, AuditTargets.Bounty, bounty.Id, staff.RequestId,
            reason: reason, before: before, after: BountyStatus.Removed.ToString());

        try
        {
            await moderation.RemoveAsync(bounty, expectedVersion, reports, audit, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("bounty.changed", "The Dicari changed while you were reading it. Open it again.");
        }

        await LapseProposalsAsync(bounty.Id, ct).ConfigureAwait(false);
        return new ChangeDone();
    }

    private async Task LapseProposalsAsync(string bountyId, CancellationToken ct)
    {
        foreach (var proposal in await bounties.ListProposalsAsync(bountyId, ct).ConfigureAwait(false))
        {
            if (proposal.Status != ProposalStatus.Pending)
            {
                continue;
            }

            var version = proposal.Version;
            proposal.Lapse("bounty.removed");
            try
            {
                await bounties.UpdateProposalAsync(proposal, version, ct).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException)
            {
                // The seller withdrew at the same moment: nothing left to lapse.
            }
        }
    }
}

/// <summary>Leaves a reported Dicari up and clears its reports, with an audit entry.</summary>
public sealed class DismissBountyReportsHandler(
    ICurrentStaff staff,
    IBountyRepository bounties,
    IBountyModerationStore moderation,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string bountyId, DismissBountyReportsRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"bounty-dismiss:{staff.StaffId}:{bountyId}",
            idempotencyKey,
            Retention,
            () => DismissAsync(bountyId, request, ct),
            ct,
            InFlightPolicy.Reject);
    }

    private async Task<ChangeDone> DismissAsync(string bountyId, DismissBountyReportsRequest request, CancellationToken ct)
    {
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 } || (note is not null && note.Any(char.IsControl)))
        {
            throw DomainException.For("bounty.removal_reason_invalid", "Keep the note under 500 characters.");
        }

        var bounty = await GetReportedBountyHandler.FindAsync(bounties, bountyId, ct).ConfigureAwait(false);
        var reports = await moderation.ListReportsAsync(bounty.Id, ct).ConfigureAwait(false);
        if (reports.Count == 0)
        {
            throw DomainException.For("bounty.no_reports", "This Dicari has no reports to dismiss.");
        }

        var audit = AuditEntry.Create(
            clock, staff.StaffId, staff.StaffName, AuditActions.BountyReportsDismissed, AuditTargets.Bounty, bounty.Id, staff.RequestId,
            reason: note, before: $"{reports.Count} reports", after: "left up");

        try
        {
            await moderation.DismissAsync(reports, audit, ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            throw DomainException.For("bounty.changed", "The Dicari changed while you were reading it. Open it again.");
        }

        return new ChangeDone();
    }
}
