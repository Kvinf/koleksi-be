using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Admin;

/// <summary>Who the admin API says the caller is. Nothing here comes from the request body.</summary>
public sealed record StaffSessionView(string StaffId, string StaffName, DateTimeOffset StartedAt);

/// <summary>
/// Records that a staff member signed in, and tells the web tier who the API believes they are.
/// </summary>
/// <remarks>
/// The web tier calls this once after a successful sign-in. It is the first audited action, and
/// it proves end to end that a staff token is accepted, that the audit write works and that the
/// caller's identity comes from the token. Retrying with the same <c>Idempotency-Key</c> returns the
/// first result and writes no second entry.
/// </remarks>
public sealed class StartStaffSessionHandler(
    ICurrentStaff staff,
    IAuditLog audit,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<StaffSessionView> HandleAsync(string? idempotencyKey, CancellationToken ct) =>
        idempotency.RunOnceAsync(
            $"admin-session:{staff.StaffId}",
            idempotencyKey,
            Retention,
            async () =>
            {
                var entry = AuditEntry.Create(
                    clock,
                    staff.StaffId,
                    staff.StaffName,
                    AuditActions.StaffSessionStarted,
                    AuditTargets.Staff,
                    staff.StaffId,
                    staff.RequestId);

                await audit.AppendAsync(entry, ct).ConfigureAwait(false);
                return new StaffSessionView(staff.StaffId, staff.StaffName, entry.At);
            },
            ct,
            InFlightPolicy.Reject);
}

/// <summary>Reads the audit log for the admin site's read-only view.</summary>
public sealed class ListAuditHandler(IAuditLog audit)
{
    public Task<AuditPage> HandleAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Limit < 1 || query.Limit > AuditQuery.MaxLimit)
        {
            throw DomainException.For("admin.audit_query_invalid", $"The page size must be 1 to {AuditQuery.MaxLimit}.");
        }

        var filters = (query.ActorId is not null ? 1 : 0) + (query.TargetId is not null || query.TargetType is not null ? 1 : 0);
        if (filters > 1)
        {
            throw DomainException.For("admin.audit_query_invalid", "Filter by the actor or by the target, not both.");
        }

        if ((query.TargetId is null) != (query.TargetType is null))
        {
            throw DomainException.For("admin.audit_query_invalid", "A target filter needs both the type and the id.");
        }

        return audit.ListAsync(query, ct);
    }
}
