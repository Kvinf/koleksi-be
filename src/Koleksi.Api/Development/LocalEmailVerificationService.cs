#if DEBUG
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Development;

/// <summary>
/// An in-memory <see cref="IEmailVerificationService"/> that logs the code instead of
/// sending an email.
/// </summary>
/// <remarks>
/// Same shortcut as <c>LocalPhoneVerificationService</c>, for the same reason: local
/// development has no mailbox to receive anything at, and pretending otherwise would
/// strand every local email-verification attempt on a code nobody ever gets. Gated the
/// same way — <c>#if DEBUG</c> plus <c>IsDevelopment()</c>, wired in <c>Program.cs</c>.
/// Follows the real adapter's rules: a challenge belongs to the account that asked, and a wrong
/// guess counts as an attempt rather than deleting it.
/// </remarks>
internal sealed partial class LocalEmailVerificationService(ILogger<LocalEmailVerificationService> logger)
    : IEmailVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Challenge> challenges = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> RequestCodeAsync(string ownerId, string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(email);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var normalized = email.Trim();

        challenges[KeyOf(ownerId, normalized)] = new Challenge(code, DateTimeOffset.UtcNow.Add(CodeLifetime));
        LogCodeIssued(logger, normalized, code);

        return Task.FromResult<string?>(code);
    }

    public Task<bool> VerifyCodeAsync(string ownerId, string email, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);

        var key = KeyOf(ownerId, email.Trim());
        if (!challenges.TryGetValue(key, out var challenge) || challenge.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Task.FromResult(false);
        }

        var attempts = Interlocked.Increment(ref challenge.Attempts);
        if (attempts > VerificationLimits.MaxAttempts)
        {
            return Task.FromResult(false);
        }

        var matches = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(challenge.Code), System.Text.Encoding.UTF8.GetBytes(code));

        if (matches || attempts >= VerificationLimits.MaxAttempts)
        {
            challenges.TryRemove(key, out _);
        }

        return Task.FromResult(matches);
    }

    private static string KeyOf(string ownerId, string email) => $"{ownerId}|{email}";

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local email-verification code for {Email}: {Code}. There is no mailbox; this is the only place it appears.")]
    private static partial void LogCodeIssued(ILogger logger, string email, string code);

    private sealed class Challenge(string code, DateTimeOffset expiresAt)
    {
        public string Code { get; } = code;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Attempts;
    }
}
#endif
