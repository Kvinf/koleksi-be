#if DEBUG
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Development;

/// <summary>
/// An in-memory <see cref="IPhoneVerificationService"/> that logs the code instead of
/// sending an SMS.
/// </summary>
/// <remarks>
/// Same shortcut as <c>LocalIdentityService</c>'s email-OTP path, for the same reason:
/// there is no phone to receive a text in local development, and pretending otherwise
/// would strand every local phone-verification attempt on a code nobody ever gets. Gated
/// the same way — <c>#if DEBUG</c> plus <c>IsDevelopment()</c>, wired in <c>Program.cs</c>.
/// </remarks>
internal sealed partial class LocalPhoneVerificationService(ILogger<LocalPhoneVerificationService> logger)
    : IPhoneVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (string Code, DateTimeOffset ExpiresAt)> challenges =
        new(StringComparer.Ordinal);

    public Task<string?> RequestCodeAsync(string phone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(phone);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var trimmedPhone = phone.Trim();

        challenges[trimmedPhone] = (code, DateTimeOffset.UtcNow.Add(CodeLifetime));
        LogCodeIssued(logger, trimmedPhone, code);

        // Handed back to the caller as well as logged — see IPhoneVerificationService's
        // doc comment on why only the dev stub ever does this.
        return Task.FromResult<string?>(code);
    }

    public Task<bool> VerifyCodeAsync(string phone, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(code);

        // One-shot, same as the real adapter: removed on every attempt regardless of match.
        if (!challenges.TryRemove(phone.Trim(), out var challenge))
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
        Message = "Local phone-verification code for {Phone}: {Code}. There is no SMS gateway; this is the only place it appears.")]
    private static partial void LogCodeIssued(ILogger logger, string phone, string code);
}
#endif
