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

    /// <summary>Address the next <see cref="RequestPasswordResetAsync"/> call "has an account" for.</summary>
    public const string KnownResetEmail = KnownEmail;

    /// <summary>Code the next <see cref="ConfirmPasswordResetAsync"/> call requires to succeed.</summary>
    public const string KnownResetCode = "654321";

    /// <summary>Current password <see cref="ChangePasswordAsync"/> requires to succeed.</summary>
    public const string KnownCurrentPassword = KnownPassword;

    /// <summary>The dev code the next reset request for the known address returns, as the local stub would.</summary>
    public string? NextResetDevCode { get; set; }

    /// <summary>When set, the next password reset/change fails with this code regardless of input.</summary>
    public string? NextPasswordFailure { get; set; }

    /// <summary>Whether <see cref="CanChangePasswordAsync"/> answers yes — false models a Google-only account.</summary>
    public bool NextCanChangePassword { get; set; } = true;

    /// <summary>Addresses <see cref="RequestPasswordResetAsync"/> was called with, in call order — registered or not.</summary>
    public List<string> ResetRequests { get; } = [];

    /// <summary>Passwords the reset/change calls accepted, in call order.</summary>
    public List<string> PasswordsSet { get; } = [];

    public Task<string?> RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        ThrowIfFailing();
        ResetRequests.Add(email);

        // Registered or not, the caller sees a completed call; only the dev code differs, and
        // only the local stub ever returns one.
        return Task.FromResult(
            string.Equals(email, KnownResetEmail, StringComparison.OrdinalIgnoreCase) ? NextResetDevCode : null);
    }

    public Task<PasswordOutcome> ConfirmPasswordResetAsync(
        string email, string code, string newPassword, CancellationToken ct)
    {
        ThrowIfFailing();

        if (NextPasswordFailure is { } failure)
        {
            return Task.FromResult(PasswordOutcome.Failed(failure));
        }

        // Unknown account and wrong code are the same answer, as the contract requires.
        var matches = string.Equals(email, KnownResetEmail, StringComparison.OrdinalIgnoreCase)
            && string.Equals(code, KnownResetCode, StringComparison.Ordinal);

        if (!matches)
        {
            return Task.FromResult(PasswordOutcome.Failed("auth.code_invalid"));
        }

        PasswordsSet.Add(newPassword);
        return Task.FromResult(PasswordOutcome.Success);
    }

    public Task<PasswordOutcome> ChangePasswordAsync(
        string accessToken, string currentPassword, string newPassword, CancellationToken ct)
    {
        ThrowIfFailing();

        if (NextPasswordFailure is { } failure)
        {
            return Task.FromResult(PasswordOutcome.Failed(failure));
        }

        if (!string.Equals(currentPassword, KnownCurrentPassword, StringComparison.Ordinal))
        {
            return Task.FromResult(PasswordOutcome.Failed("auth.current_password_invalid"));
        }

        PasswordsSet.Add(newPassword);
        return Task.FromResult(PasswordOutcome.Success);
    }

    public Task<bool> CanChangePasswordAsync(string accessToken, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextCanChangePassword);
    }

    private static AuthenticatedSession Session() => new(
        "header.payload.signature",
        "valid-refresh",
        DateTimeOffset.UtcNow.AddHours(1),
        KnownUsername);
}
