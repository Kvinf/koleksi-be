using Koleksi.Domain.Common;

namespace Koleksi.Domain.Admin;

/// <summary>
/// The free-text reason (or note) a staff action carries. One rule for every action, so the pages can say it once.
/// </summary>
/// <remarks>
/// Five to a thousand characters after trimming: shorter than five is not a reason, and a thousand is the same bound the
/// public site puts on a buyer's complaint, so what staff write is never longer than what they are answering.
/// </remarks>
public static class AdminReason
{
    public const int MinLength = 5;

    public const int MaxLength = 1000;

    /// <summary>Returns the trimmed text, or throws <c>admin.reason_invalid</c>.</summary>
    public static string Require(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        // Line breaks are fine in a reason; a NUL is never text.
        if (trimmed.Length is < MinLength or > MaxLength || trimmed.Contains('\0', StringComparison.Ordinal))
        {
            throw DomainException.For(
                "admin.reason_invalid",
                $"A reason must be {MinLength} to {MaxLength} characters.");
        }

        return trimmed;
    }
}
