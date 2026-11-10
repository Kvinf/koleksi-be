using Koleksi.Domain.Admin;
using Koleksi.Domain.Bounties;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Reports on Dicari, and the staff changes that settle them.
/// </summary>
/// <remarks>
/// A report is one row in the Dicari's own partition, and unsettled reports also sit in a sparse queue index staff read
/// newest first. Settling is one transaction: the change to the Dicari (or none, for a dismissal), the deletion of the
/// reports it settles, and the audit entry, together or not at all. Nothing here scans.
/// </remarks>
public interface IBountyModerationStore
{
    /// <summary>
    /// Stores a report. Returns <c>false</c> when this person already reported the Dicari, or it already carries
    /// <see cref="BountyReport.MaxPerBounty"/> reports.
    /// </summary>
    Task<bool> AddReportAsync(BountyReport report, CancellationToken ct);

    /// <summary>The unsettled reports on one Dicari, oldest first.</summary>
    Task<IReadOnlyList<BountyReport>> ListReportsAsync(string bountyId, CancellationToken ct);

    /// <summary>The unsettled reports across Dicari, newest first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<BountyReport>> ListQueueAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Writes a removal: the Dicari's new state, the reports it settles deleted, and the audit entry.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The Dicari changed since it was read.</exception>
    Task RemoveAsync(Bounty bounty, long expectedVersion, IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct);

    /// <summary>Deletes the reports on a Dicari that staff decided to leave up, with the audit entry.</summary>
    Task DismissAsync(IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct);
}
