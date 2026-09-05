using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IIdentityService"/>.
/// </summary>
/// <remarks>
/// Holds one account. The endpoint tests are about what the API says on the way out —
/// whether a wrong password and an unknown account are answered identically, whether the
/// refresh token reaches the caller, whether the rate limit bites — none of which needs a
/// real user pool, and all of which a real one would make slower and less deterministic.
/// </remarks>
public sealed class FakeIdentityService : FakeAdapter, IIdentityService
{
    public const string KnownEmail = "budi@example.test";
    public const string KnownPassword = "correct-horse-battery-staple";
    public const string KnownUsername = "budi.collectibles";

    /// <summary>Registration outcome the next call returns.</summary>
    public RegistrationOutcome NextRegistration { get; set; } = RegistrationOutcome.AwaitingConfirmation;

    /// <summary>Confirmation outcome the next <see cref="ConfirmSignUpAsync"/> call returns.</summary>
    public ConfirmOutcome NextConfirmation { get; set; } = ConfirmOutcome.Success;

    /// <summary>Result the next <see cref="ResendConfirmationCodeAsync"/> call returns.</summary>
    public bool NextResendSucceeds { get; set; } = true;

    /// <summary>OTP challenge the next <see cref="RequestEmailOtpAsync"/> call returns.</summary>
    public OtpChallenge? NextOtpChallenge { get; set; } = new("otp-session", "123456");

    /// <summary>Session the next <see cref="VerifyEmailOtpAsync"/> call requires to succeed.</summary>
    public const string KnownOtpSession = "otp-session";

    /// <summary>Code the next <see cref="VerifyEmailOtpAsync"/> call requires to succeed.</summary>
    public const string KnownOtpCode = "123456";

    /// <summary>
    /// When set, a matching session and code return this instead of a session — the
    /// "code was right, account isn't confirmed" branch.
    /// </summary>
    public string? NextOtpUnconfirmedUsername { get; set; }

    /// <summary>Authorization code the next <see cref="ExchangeAuthorizationCodeAsync"/> call requires to succeed.</summary>
    public const string KnownGoogleCode = "valid-google-code";

    /// <summary>Email the next <see cref="GetEmailAsync"/> call returns, for any token.</summary>
    public string? NextEmail { get; set; } = KnownEmail;

    /// <summary>Refresh tokens passed to <see cref="SignOutAsync"/>, in call order.</summary>
    public List<string> Revoked { get; } = [];

    public Task<AuthenticatedSession?> SignInAsync(string email, string password, CancellationToken ct)
    {
        ThrowIfFailing();

        var matches = string.Equals(email, KnownEmail, StringComparison.OrdinalIgnoreCase)
            && string.Equals(password, KnownPassword, StringComparison.Ordinal);

        return Task.FromResult(matches ? Session() : null);
    }

    public Task<RegistrationOutcome> RegisterAsync(
        string username, string email, string password, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextRegistration);
    }

    public Task<AuthenticatedSession?> RefreshAsync(
        string refreshToken, string username, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(refreshToken == "valid-refresh" ? Session() : null);
    }

    public Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        ThrowIfFailing();
        Revoked.Add(refreshToken);
        return Task.CompletedTask;
    }

    public Task<ConfirmOutcome> ConfirmSignUpAsync(string username, string code, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextConfirmation);
    }

    public Task<bool> ResendConfirmationCodeAsync(string username, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextResendSucceeds);
    }

    public Task<OtpChallenge?> RequestEmailOtpAsync(string email, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextOtpChallenge);
    }

    public Task<OtpVerifyOutcome> VerifyEmailOtpAsync(
        string email, string session, string code, CancellationToken ct)
    {
        ThrowIfFailing();

        var matches = string.Equals(session, KnownOtpSession, StringComparison.Ordinal)
            && string.Equals(code, KnownOtpCode, StringComparison.Ordinal);

        if (!matches)
        {
            return Task.FromResult(OtpVerifyOutcome.Failed);
        }

        return Task.FromResult(
            NextOtpUnconfirmedUsername is { } username
                ? OtpVerifyOutcome.NotConfirmed(username)
                : OtpVerifyOutcome.Success(Session()));
    }

    public Task<AuthenticatedSession?> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(code == KnownGoogleCode ? Session() : null);
    }

    public Task<string?> GetEmailAsync(string accessToken, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextEmail);
    }

    private static AuthenticatedSession Session() => new(
        "header.payload.signature",
        "valid-refresh",
        DateTimeOffset.UtcNow.AddHours(1),
        KnownUsername);
}
