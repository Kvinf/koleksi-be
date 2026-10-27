using System.Text.RegularExpressions;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Admin;

/// <summary>
/// One thing a staff member did, recorded for good.
/// </summary>
/// <remarks>
/// <para>
/// An entry is immutable and has no update or delete path anywhere in the code base. It is written in
/// the same DynamoDB transaction as the change it describes, so there is no state in which the change
/// happened and the entry did not (or the other way round).
/// </para>
/// <para>
/// <b>Who</b> is the staff member's id (the <c>sub</c> of the staff pool's access token) and their sign-in
/// name. There is no email hash: the access token carries none, and staff accounts have no email-based
/// recovery to attach one to. The raw token is never stored.
/// </para>
/// <para>
/// <b>What</b> is a dotted <see cref="Action"/> such as <c>staff.session_started</c>, a <see cref="TargetType"/>
/// and <see cref="TargetId"/>, a free-text <see cref="Reason"/> where the action asks for one, and short
/// <see cref="Before"/>/<see cref="After"/> summaries. The summaries are for a human reading the log, so
/// they must be written without secrets, bank numbers or document keys.
/// </para>
/// </remarks>
public sealed partial record AuditEntry
{
    public const int MaxReasonLength = 1000;

    public const int MaxSummaryLength = 1000;

    public const int MaxIdLength = 128;

    private AuditEntry()
    {
    }

    public required string Id { get; init; }

    public required DateTimeOffset At { get; init; }

    public required string ActorId { get; init; }

    public required string ActorName { get; init; }

    public required string Action { get; init; }

    public required string TargetType { get; init; }

    public required string TargetId { get; init; }

    public string? Reason { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }

    public required string RequestId { get; init; }

    /// <summary>Builds a validated entry. Throws <see cref="DomainException"/> (<c>admin.audit_invalid</c>) when a field is unusable.</summary>
    public static AuditEntry Create(
        IClock clock,
        string actorId,
        string actorName,
        string action,
        string targetType,
        string targetId,
        string requestId,
        string? reason = null,
        string? before = null,
        string? after = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RequireIdentifier(actorId, "actor id");
        RequireIdentifier(targetId, "target id");
        RequireIdentifier(requestId, "request id");
        RequireName(actorName, "actor name");

        if (!ActionPattern().IsMatch(action ?? string.Empty))
        {
            throw Invalid("The action must be lower-case words joined by dots or underscores, 3 to 64 characters.");
        }

        if (!ActionPattern().IsMatch(targetType ?? string.Empty))
        {
            throw Invalid("The target type must be lower-case words joined by dots or underscores, 3 to 64 characters.");
        }

        RequireLength(reason, MaxReasonLength, "reason");
        RequireLength(before, MaxSummaryLength, "before summary");
        RequireLength(after, MaxSummaryLength, "after summary");

        var now = clock.UtcNow;
        return new AuditEntry
        {
            Id = EntityId.New(now),
            At = now,
            ActorId = actorId,
            ActorName = actorName.Trim(),
            Action = action!,
            TargetType = targetType!,
            TargetId = targetId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Before = string.IsNullOrWhiteSpace(before) ? null : before.Trim(),
            After = string.IsNullOrWhiteSpace(after) ? null : after.Trim(),
            RequestId = requestId,
        };
    }

    /// <summary>
    /// Rebuilds an entry from a stored row. For the persistence layer only: nothing is re-validated or
    /// re-stamped, because a row written under an older rule must still be readable.
    /// </summary>
    public static AuditEntry Restore(
        string id,
        DateTimeOffset at,
        string actorId,
        string actorName,
        string action,
        string targetType,
        string targetId,
        string requestId,
        string? reason,
        string? before,
        string? after) =>
        new()
        {
            Id = id,
            At = at,
            ActorId = actorId,
            ActorName = actorName,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            RequestId = requestId,
            Reason = reason,
            Before = before,
            After = after,
        };

    [GeneratedRegex("^[a-z][a-z0-9_.]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();

    private static void RequireIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength || HasControlCharacter(value))
        {
            throw Invalid($"The {name} is required, at most {MaxIdLength} characters, with no control characters.");
        }
    }

    private static void RequireName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength || HasControlCharacter(value))
        {
            throw Invalid($"The {name} is required, at most {MaxIdLength} characters, with no control characters.");
        }
    }

    private static void RequireLength(string? value, int max, string name)
    {
        if (value is not null && value.Length > max)
        {
            throw Invalid($"The {name} must be at most {max} characters.");
        }
    }

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

    private static DomainException Invalid(string message) => DomainException.For("admin.audit_invalid", message);
}

/// <summary>The actions the audit log knows by name. A new milestone adds its own here, never as a string at the call site.</summary>
public static class AuditActions
{
    /// <summary>A staff member signed in and their session was accepted by the admin API.</summary>
    public const string StaffSessionStarted = "staff.session_started";

    /// <summary>Staff added an internal note to a dispute.</summary>
    public const string DisputeNoteAdded = "dispute.note_added";

    /// <summary>Staff asked the buyer or the seller for more information.</summary>
    public const string DisputeInfoRequested = "dispute.info_requested";

    public const string DisputeResolvedForSeller = "dispute.resolved_for_seller";

    public const string DisputeResolvedForBuyer = "dispute.resolved_for_buyer";

    /// <summary>Staff decided a remainder they had held: released it to the seller or split it.</summary>
    public const string DisputeRemainderSettled = "dispute.remainder_settled";

    /// <summary>Staff read the order's private chat while reviewing a case.</summary>
    public const string DisputeChatViewed = "dispute.chat_viewed";

    public const string HolidayAdded = "holiday.added";

    public const string HolidayRemoved = "holiday.removed";

    /// <summary>The system wrote a year's official holidays for the first time (actor: system).</summary>
    public const string HolidaySeeded = "holiday.seeded";
}

/// <summary>The kinds of thing an audit entry can point at.</summary>
public static class AuditTargets
{
    /// <summary>A staff member (the actor acting on their own session).</summary>
    public const string Staff = "staff";

    public const string Order = "order";

    public const string Holiday = "holiday";
}
