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
/// </remarks>
internal sealed partial class LocalEmailVerificationService(ILogger<LocalEmailVerificationService> logger)
    : IEmailVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string Code, DateTimeOffset ExpiresAt)> challenges =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> RequestCodeAsync(string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var normalized = email.Trim();

        challenges[normalized] = (code, DateTimeOffset.UtcNow.Add(CodeLifetime));
        LogCodeIssued(logger, normalized, code);

        return Task.FromResult<string?>(code);
    }

    public Task<bool> VerifyCodeAsync(string email, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(code);

        // One-shot, same as the real adapter: removed on every attempt regardless of match.
        if (!challenges.TryRemove(email.Trim(), out var challenge))
        {
            return Task.FromResult(false);
        }

        var matches = challenge.ExpiresAt >= DateTimeOffset.UtcNow
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(challenge.Code), System.Text.Encoding.UTF8.GetBytes(code));

        return Task.FromResult(matches);
    }

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local email-verification code for {Email}: {Code}. There is no mailbox; this is the only place it appears.")]
    private static partial void LogCodeIssued(ILogger logger, string email, string code);
}
#endif
