using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Bounties;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// In-memory report store. Applies a removal to the Dicari store it is given and hands the audit entry to a callback, so a
/// test sees the change and its audit entry land together, as the real transaction does.
/// </summary>
public sealed class FakeBountyModerationStore(FakeBountyRepository bounties, Action<AuditEntry>? onAudit = null)
    : FakeAdapter, IBountyModerationStore
{
    private readonly List<BountyReport> _reports = [];
    private readonly object _gate = new();

    public IReadOnlyList<BountyReport> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _reports];
            }
        }
    }

    public void Seed(BountyReport report)
    {
        lock (_gate)
        {
            _reports.Add(report);
        }
    }

    public Task<bool> AddReportAsync(BountyReport report, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var mine = _reports.Count(r => r.BountyId == report.BountyId);
            if (mine >= BountyReport.MaxPerBounty
                || _reports.Any(r => r.BountyId == report.BountyId && r.ReporterId == report.ReporterId))
            {
                return Task.FromResult(false);
            }

            _reports.Add(report);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<BountyReport>> ListReportsAsync(string bountyId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<BountyReport> items = [.. _reports.Where(r => r.BountyId == bountyId).OrderBy(r => r.CreatedAt)];
            return Task.FromResult(items);
        }
    }

    public Task<IReadOnlyList<BountyReport>> ListQueueAsync(int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<BountyReport> items = [.. _reports.OrderByDescending(r => r.CreatedAt).Take(limit)];
            return Task.FromResult(items);
        }
    }

    public async Task RemoveAsync(
        Bounty bounty, long expectedVersion, IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct)
    {
        ThrowIfFailing();
        await bounties.UpdateAsync(bounty, expectedVersion, ct).ConfigureAwait(false);
        Settle(settled, audit);
    }

    public Task DismissAsync(IReadOnlyList<BountyReport> settled, AuditEntry audit, CancellationToken ct)
    {
        ThrowIfFailing();
        Settle(settled, audit);
        return Task.CompletedTask;
    }

    private void Settle(IReadOnlyList<BountyReport> settled, AuditEntry audit)
    {
        lock (_gate)
        {
            _reports.RemoveAll(r => settled.Any(s => s.BountyId == r.BountyId && s.ReporterId == r.ReporterId));
        }

        onAudit?.Invoke(audit);
    }
}
