using Koleksi.Domain.Common;

namespace Koleksi.Domain.Admin;

/// <summary>
/// The rule that a staff member cannot act on their own account.
/// </summary>
/// <remarks>
/// <para>
/// There is one role (everyone in the staff pool can do everything), so nothing else limits what a
/// signed-in staff member may do. This is one of the few brakes that are left: a person cannot lift their
/// own ban, approve their own ID check or resolve a dispute about their own order.
/// </para>
/// <para>
/// <b>What it cannot see.</b> Staff ids come from a separate user pool and never name a marketplace
/// account, so a staff member who also has a public account has a different id there. Comparing ids
/// therefore stops only the case where the two ids are the same; linking a staff account to the person's
/// public account is an open question for the owner (see docs/admin-site-proposal.md).
/// </para>
/// </remarks>
public static class StaffGuard
{
    /// <summary>Refuses (<c>admin.cannot_act_on_self</c>) when the target is the acting staff member.</summary>
    public static void EnsureNotSelf(string actorId, string targetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (string.Equals(actorId, targetId, StringComparison.Ordinal))
        {
            throw DomainException.For("admin.cannot_act_on_self", "A staff member cannot act on their own account.");
        }
    }

    /// <summary>
    /// Refuses (<c>admin.cannot_act_on_staff</c>) when the target is any known staff member.
    /// </summary>
    /// <param name="staffIds">The ids of every staff account the caller knows of; compared exactly.</param>
    /// <param name="targetId">The id the action is aimed at.</param>
    public static void EnsureNotStaff(IReadOnlySet<string> staffIds, string targetId)
    {
        ArgumentNullException.ThrowIfNull(staffIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (staffIds.Contains(targetId))
        {
            throw DomainException.For("admin.cannot_act_on_staff", "A staff member cannot act on another staff account.");
        }
    }
}
