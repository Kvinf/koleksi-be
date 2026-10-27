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
/// Follows the real adapter's rules: a challenge belongs to the account that asked, and a wrong
/// guess counts as an attempt rather than deleting it.
/// </remarks>
internal sealed partial class LocalPhoneVerificationService(ILogger<LocalPhoneVerificationService> logger)
    : IPhoneVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Challenge> challenges = new(StringComparer.Ordinal);

    public Task<string?> RequestCodeAsync(string ownerId, string phone, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(phone);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var trimmedPhone = phone.Trim();

        challenges[KeyOf(ownerId, trimmedPhone)] = new Challenge(code, DateTimeOffset.UtcNow.Add(CodeLifetime));
        LogCodeIssued(logger, trimmedPhone, code);

        // Handed back to the caller as well as logged — see IPhoneVerificationService's
        // doc comment on why only the dev stub ever does this.
        return Task.FromResult<string?>(code);
    }

    public Task<bool> VerifyCodeAsync(string ownerId, string phone, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        ArgumentNullException.ThrowIfNull(phone);
        ArgumentNullException.ThrowIfNull(code);

        var key = KeyOf(ownerId, phone.Trim());
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

    private static string KeyOf(string ownerId, string phone) => $"{ownerId}|{phone}";

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local phone-verification code for {Phone}: {Code}. There is no SMS gateway; this is the only place it appears.")]
    private static partial void LogCodeIssued(ILogger logger, string phone, string code);

    private sealed class Challenge(string code, DateTimeOffset expiresAt)
    {
        public string Code { get; } = code;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Attempts;
    }
}
#endif
