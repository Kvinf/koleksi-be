#if DEBUG
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Koleksi.Api.Development;

/// <summary>
/// An <see cref="IIdentityService"/> that mints real, locally signed tokens.
/// </summary>
/// <remarks>
/// See <see cref="LocalIdentity"/> for what gates this on and why. Accounts persist to the
/// local DynamoDB table via <see cref="LocalAccountStore"/> — see its remarks for exactly
/// what that does and does not survive. Refresh tokens, OTP challenges, and pending
/// sign-up confirmation codes stay in the dictionaries below regardless: each process gets
/// a fresh <see cref="LocalSigningKey"/>, so a session from before a restart could not be
/// used again even if these outlived it.
/// </remarks>
internal sealed partial class LocalIdentityService(
    LocalSigningKey signingKey,
    string issuer,
    string clientId,
    LocalAccountStore accountStore,
    ILogger<LocalIdentityService> logger) : IIdentityService
{
    /// <summary>Matches the access-token lifetime a Cognito pool issues by default.</summary>
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>Refresh token to the email that owns it.</summary>
    private readonly ConcurrentDictionary<string, string> refreshTokens =
        new(StringComparer.Ordinal);

    /// <summary>Live OTP challenges, keyed by the opaque session token handed to the client.</summary>
    private readonly ConcurrentDictionary<string, OtpState> otpChallenges =
        new(StringComparer.Ordinal);

    /// <summary>Live sign-up confirmation codes, keyed by username.</summary>
    private readonly ConcurrentDictionary<string, ConfirmationState> confirmations =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a sign-up confirmation code stays usable. Longer than <see cref="OtpLifetime"/>
    /// on purpose — a login OTP is meant to be typed in immediately, but a real inbox check
    /// after registering is the whole thing this stub can't reproduce, so it errs generous
    /// rather than expiring a code before a developer has finished reading this file's log line.
    /// </summary>
    private static readonly TimeSpan ConfirmationCodeLifetime = TimeSpan.FromMinutes(15);

    public async Task<AuthenticatedSession?> SignInAsync(string email, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var account = await accountStore.FindByEmailAsync(email.Trim(), ct).ConfigureAwait(false);

        /*
         * One answer for every failure, as the interface requires: no such account and
         * wrong password are indistinguishable from outside. Worth keeping even here —
         * a developer reading local behaviour should not learn a contract that differs
         * from the one the deployed adapter honours.
         */
        if (account is null || !Matches(account.Password, password))
        {
            return null;
        }

        // Mirrors the pool this stands in for: autoVerify:{email:true} means a self-signed-up
        // account is UNCONFIRMED until the emailed code is submitted, and every sign-in
        // attempt fails until then — indistinguishably from a wrong password, same reasoning
        // as everything else this method refuses to distinguish.
        if (!account.Confirmed)
        {
            return null;
        }

        return IssueSession(account);
    }

    public async Task<RegistrationOutcome> RegisterAsync(
        string username, string email, string password, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        // The pool's own policy, applied here so a password refused in a deployed stage is
        // refused locally too. The breached-password check is the one rule that cannot be
        // reproduced: it is a Cognito service feature rather than a rule we hold.
        if (!IsPasswordAcceptable(password))
        {
            return RegistrationOutcome.Failed("auth.password_too_weak");
        }

        var handle = username.Trim();

        if (await accountStore.FindByUsernameAsync(handle, ct).ConfigureAwait(false) is not null)
        {
            return RegistrationOutcome.Failed("auth.username_taken");
        }

        var account = new LocalAccount(EntityId.New(), handle, email.Trim(), password, Confirmed: false);

        if (!await accountStore.TryCreateAsync(account, ct).ConfigureAwait(false))
        {
            // A second registration for the same address. A distinct code from
            // auth.username_taken — that one names the username specifically because a
            // handle is public (it's the shop name), but the username just passed its own
            // uniqueness check above, so blaming it here would be wrong. This code stays
            // generic enough that it does not confirm the email is already registered.
            return RegistrationOutcome.Failed("auth.registration_failed");
        }

        LogRegistered(logger, account.Username, account.Email);

        /*
         * AwaitingConfirmation, mirroring the real pool's autoVerify:{email:true} behaviour
         * rather than short-circuiting it: a code is generated and logged the same way
         * RequestEmailOtpAsync logs its login code, and handed back as DevCode so the
         * frontend can render it directly — there is no mailbox here to deliver it to, and
         * pretending the account is already confirmed would leave the confirmation screen
         * this app renders untested by anyone working locally.
         */
        var code = GenerateCode();
        confirmations[account.Username] = new ConfirmationState(account.Email, code, DateTimeOffset.UtcNow.Add(ConfirmationCodeLifetime));
        LogConfirmationCodeIssued(logger, account.Email, code);

        return RegistrationOutcome.AwaitingConfirmationWithCode(code);
    }

    public async Task<AuthenticatedSession?> RefreshAsync(
        string refreshToken, string username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        ArgumentNullException.ThrowIfNull(username);

        if (!refreshTokens.TryGetValue(refreshToken, out var email))
        {
            return null;
        }

        var account = await accountStore.FindByEmailAsync(email, ct).ConfigureAwait(false);

        if (account is null || !string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The same refresh token stays valid, as Cognito's does — it does not reissue one
        // on every exchange, and the web tier's session code is written for that.
        return IssueSession(account, refreshToken);
    }

    public Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        refreshTokens.TryRemove(refreshToken, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks the code <see cref="RegisterAsync"/> generated, the same way a real pool would
    /// check the one it emailed.
    /// </summary>
    public async Task<ConfirmOutcome> ConfirmSignUpAsync(string username, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(code);

        var trimmedUsername = username.Trim();
        var account = await accountStore.FindByUsernameAsync(trimmedUsername, ct).ConfigureAwait(false);

        if (account is null)
        {
            // No such account. Answered with the same codes a real mismatch would get —
            // this endpoint must not become a way to test whether a username exists.
            return ConfirmOutcome.Failed("auth.code_invalid");
        }

        if (account.Confirmed)
        {
            return ConfirmOutcome.Failed("auth.already_confirmed");
        }

        if (!confirmations.TryGetValue(trimmedUsername, out var pending) || pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return ConfirmOutcome.Failed("auth.code_expired");
        }

        if (!Matches(pending.Code, code.Trim()))
        {
            return ConfirmOutcome.Failed("auth.code_invalid");
        }

        // One-shot, same as an OTP challenge: removed on success so the code cannot be
        // replayed, and the account record is swapped for a confirmed copy rather than
        // mutated in place, since the record itself is immutable.
        confirmations.TryRemove(trimmedUsername, out _);
        await accountStore.SaveAsync(account with { Confirmed = true }, ct).ConfigureAwait(false);

        return ConfirmOutcome.Success;
    }

    /// <summary>Regenerates and re-logs the code, the same way the first one was issued.</summary>
    public async Task<bool> ResendConfirmationCodeAsync(string username, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(username);

        var trimmedUsername = username.Trim();
        var account = await accountStore.FindByUsernameAsync(trimmedUsername, ct).ConfigureAwait(false);

        if (account is null || account.Confirmed)
        {
            return false;
        }

        var code = GenerateCode();
        confirmations[trimmedUsername] = new ConfirmationState(account.Email, code, DateTimeOffset.UtcNow.Add(ConfirmationCodeLifetime));
        LogConfirmationCodeIssued(logger, account.Email, code);

        return true;
    }

    /// <summary>
    /// "Sends" a code by generating one and handing it straight back in
    /// <see cref="OtpChallenge.DevCode"/> — there is no mailbox here to deliver it to, and
    /// pretending otherwise would strand every local sign-in on a code nobody ever
    /// receives.
    /// </summary>
    public async Task<OtpChallenge?> RequestEmailOtpAsync(string email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);

        var session = NewRefreshToken();
        var trimmed = email.Trim();
        var exists = await accountStore.FindByEmailAsync(trimmed, ct).ConfigureAwait(false) is not null;

        /*
         * A session is issued whether or not the account exists, mirroring the real
         * adapter's anti-enumeration contract — the difference has to be invisible to
         * whoever is looking at the response. What differs is only what verifying it can
         * ever do: a session for an address with no account carries a code that can never
         * match, exactly as if Cognito had emailed a real code to a mailbox nobody reads.
         */
        var code = exists ? GenerateCode() : "000000";
        otpChallenges[session] = new OtpState(trimmed, code, DateTimeOffset.UtcNow.Add(OtpLifetime), exists);

        if (exists)
        {
            LogOtpIssued(logger, trimmed, code);
        }

        return new OtpChallenge(session, exists ? code : null);
    }

    public async Task<OtpVerifyOutcome> VerifyEmailOtpAsync(
        string email, string session, string code, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(code);

        // One-shot: removed from the dictionary here on every call, matched or not, so a
        // captured code cannot be replayed even within its five-minute window.
        if (!otpChallenges.TryRemove(session, out var state)
            || state.ExpiresAt < DateTimeOffset.UtcNow
            || !state.AccountExists
            || !string.Equals(state.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)
            || !Matches(state.Code, code))
        {
            return OtpVerifyOutcome.Failed;
        }

        var account = await accountStore.FindByEmailAsync(state.Email, ct).ConfigureAwait(false);

        if (account is null)
        {
            return OtpVerifyOutcome.Failed;
        }

        // Same rule as the password path: an unconfirmed account signs in through neither
        // door until the emailed code from registration is submitted. Named specifically —
        // see OtpVerifyOutcome.UnconfirmedUsername — rather than folded into Failed above,
        // because every check up to here already passed: this branch only runs for someone
        // who already proved they hold the real code.
        if (!account.Confirmed)
        {
            return OtpVerifyOutcome.NotConfirmed(account.Username);
        }

        return OtpVerifyOutcome.Success(IssueSession(account));
    }

    /// <summary>
    /// Signs in a fixed <c>google-dev@example.com</c> account immediately, ignoring
    /// <paramref name="code"/>/<paramref name="redirectUri"/>/<paramref name="codeVerifier"/>.
    /// </summary>
    /// <remarks>
    /// There is no hosted UI and no Google to redirect to in local development — the
    /// frontend's <c>auth.google.tsx</c>/<c>auth.callback.tsx</c> pair still runs its own
    /// PKCE round trip against nothing in particular, but whatever it hands back is accepted
    /// unconditionally. Documented as a shortcut, not a security control: this type is
    /// unreachable outside a Debug build, the same gate every other dev-only behaviour on
    /// this class relies on.
    /// </remarks>
    public async Task<AuthenticatedSession?> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(codeVerifier);

        var account = await accountStore.FindByEmailAsync(GoogleDevEmail, ct).ConfigureAwait(false);

        if (account is null)
        {
            // Confirmed: true — federated sign-in has no confirmation step to skip, here or
            // against a real pool, since Google already vouched for the address. A lost
            // race with a concurrent first click just means the loser reads back what the
            // winner created, via the FindByEmailAsync below — there is only ever one
            // Google dev account, so this cannot end up creating two.
            account = new LocalAccount(EntityId.New(), "google-dev", GoogleDevEmail, NewRefreshToken(), Confirmed: true);

            if (!await accountStore.TryCreateAsync(account, ct).ConfigureAwait(false))
            {
                account = await accountStore.FindByEmailAsync(GoogleDevEmail, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Google dev account creation lost a race but the winner's write is not visible yet.");
            }
        }

        return IssueSession(account);
    }

    /// <summary>The fixed account every local "Continue with Google" click signs into.</summary>
    private const string GoogleDevEmail = "google-dev@example.com";

    /// <summary>
    /// Decodes the token's <c>sub</c> claim and looks up the matching account — no
    /// signature check, because this process is the one that signed it moments ago and
    /// the real adapter's equivalent (<c>GetUser</c>) trusts Cognito's own validation the
    /// same way rather than re-verifying a token it already accepted.
    /// </summary>
    public async Task<string?> GetEmailAsync(string accessToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(accessToken);

        var subject = Handler.ReadJsonWebToken(accessToken).GetClaim("sub").Value;
        var account = await accountStore.FindBySubjectAsync(subject, ct).ConfigureAwait(false);

        return account?.Email;
    }

    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The user pool's password policy, mirrored from <c>infra/lib/auth-stack.ts</c>.
    /// </summary>
    /// <remarks>
    /// The sign-up form checks the same five rules in the browser
    /// (<c>frontend/app/lib/password.ts</c>). A third copy is one more than anybody wants,
    /// and it is the copy that makes local development behave like a deployed stage.
    /// </remarks>
    private static bool IsPasswordAcceptable(string password) =>
        password.Length >= 8
        && password.Any(char.IsAsciiLetterUpper)
        && password.Any(char.IsAsciiLetterLower)
        && password.Any(char.IsAsciiDigit)
        && password.Any(ch => !char.IsAsciiLetterOrDigit(ch));

    /// <summary>Constant-time comparison, so local behaviour models the real thing.</summary>
    private static bool Matches(string stored, string supplied) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(supplied));

    private AuthenticatedSession IssueSession(LocalAccount account, string? existingRefreshToken = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(AccessTokenLifetime);
        var refreshToken = existingRefreshToken ?? NewRefreshToken();

        refreshTokens[refreshToken] = account.Email;

        return new AuthenticatedSession(
            MintAccessToken(account, expiresAt), refreshToken, expiresAt, account.Username);
    }

    /// <summary>An access token shaped exactly as Cognito shapes one.</summary>
    /// <remarks>
    /// Every claim the API reads is present and carries the value the real handler
    /// validates against: the derived issuer, the configured <c>client_id</c>, and
    /// <c>token_use=access</c>, so an id token is still refused as a credential.
    /// </remarks>
    private string MintAccessToken(LocalAccount account, DateTimeOffset expiresAt)
    {
        var issuedAt = DateTimeOffset.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey.Key, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = account.Subject,
                ["cognito:username"] = account.Username,
                ["client_id"] = clientId,
                ["token_use"] = "access",
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };

        return Handler.CreateToken(descriptor);
    }

    private static string NewRefreshToken() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <param name="AccountExists">
    /// Whether <paramref name="Email"/> named a real account at the moment the code was
    /// issued. Carried alongside the code, not derived by looking the account up again at
    /// verify time, so that an account registered *between* request and verify cannot
    /// retroactively make an otherwise-unmatchable session start matching.
    /// </param>
    private sealed record OtpState(string Email, string Code, DateTimeOffset ExpiresAt, bool AccountExists);

    /// <summary>A sign-up confirmation code awaiting <see cref="ConfirmSignUpAsync"/>.</summary>
    private sealed record ConfirmationState(string Email, string Code, DateTimeOffset ExpiresAt);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local account registered: {Username} <{Email}>. It lives until this process exits.")]
    private static partial void LogRegistered(ILogger logger, string username, string email);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local email-OTP issued for {Email}: {Code}. There is no mailbox; this is the only place it appears.")]
    private static partial void LogOtpIssued(ILogger logger, string email, string code);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Local sign-up confirmation code issued for {Email}: {Code}. There is no mailbox; this is the only place it appears.")]
    private static partial void LogConfirmationCodeIssued(ILogger logger, string email, string code);
}
#endif
