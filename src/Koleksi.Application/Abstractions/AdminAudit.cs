using Koleksi.Domain.Admin;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// The signed-in staff member making the current admin request, as established from the staff pool's
/// validated access token.
/// </summary>
/// <remarks>
/// A separate interface from <see cref="ICurrentUser"/> on purpose: that one describes a marketplace
/// account from the public pool, this one a staff member from the staff pool, and a handler that takes
/// the wrong one should not compile.
/// </remarks>
public interface ICurrentStaff
{
    /// <summary>The staff member's id (the token's <c>sub</c>).</summary>
    string StaffId { get; }

    /// <summary>The staff member's sign-in name, for the audit log and the page header.</summary>
    string StaffName { get; }

    /// <summary>Ties the request to its log lines and to the audit entry it writes.</summary>
    string RequestId { get; }
}

/// <summary>What the audit view can be narrowed by. Everything is optional; combine none, or exactly one filter.</summary>
/// <param name="ActorId">Only entries by this staff member.</param>
/// <param name="TargetType">Only entries about this kind of thing; needs <paramref name="TargetId"/> as well.</param>
/// <param name="TargetId">Only entries about this one thing; needs <paramref name="TargetType"/> as well.</param>
/// <param name="Cursor">The <see cref="AuditPage.NextCursor"/> of the previous page.</param>
/// <param name="Limit">Page size, 1 to <see cref="AuditQuery.MaxLimit"/>.</param>
public sealed record AuditQuery(string? ActorId, string? TargetType, string? TargetId, string? Cursor, int Limit)
{
    public const int MaxLimit = 100;

    public const int DefaultLimit = 50;
}

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, string? NextCursor);

/// <summary>
/// The append-only record of staff actions.
/// </summary>
/// <remarks>
/// There is deliberately no update or delete here, or anywhere else. An action that changes data does not
/// call <see cref="AppendAsync"/>: its repository writes the entry in the same transaction as the change
/// (see <c>AuditItems</c> in the infrastructure layer). <see cref="AppendAsync"/> is for an event that has
/// no other data to change, such as a staff member signing in.
/// </remarks>
public interface IAuditLog
{
    /// <summary>Writes one entry. Never overwrites an existing one.</summary>
    Task AppendAsync(AuditEntry entry, CancellationToken ct);

    /// <summary>Newest first. Reads a single partition per page, never scans.</summary>
    Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct);
}
