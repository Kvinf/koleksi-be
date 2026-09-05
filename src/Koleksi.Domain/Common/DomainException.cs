namespace Koleksi.Domain.Common;

/// <summary>
/// Raised when an operation would violate a business rule the domain guarantees —
/// bidding below the minimum increment, releasing escrow on an undelivered order,
/// accepting an expired offer.
/// </summary>
/// <remarks>
/// The API translates this into a <c>409 Conflict</c> with a stable
/// <see cref="Code"/> the frontend can switch on. The <see cref="Exception.Message"/>
/// is developer-facing and is never shown to end users; user-facing copy is
/// resolved from <see cref="Code"/> in the frontend so it can be translated to
/// Indonesian and English.
/// </remarks>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message) : base(message) => Code = code;

    /// <summary>A stable, machine-readable identifier such as <c>auction.bid_too_low</c>.</summary>
    public string Code { get; }

    public static DomainException For(string code, string message) => new(code, message);
}
