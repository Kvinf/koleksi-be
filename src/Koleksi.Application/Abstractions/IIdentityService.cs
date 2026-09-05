namespace Koleksi.Application.Abstractions;

/// <summary>
/// Exchanges credentials for tokens against the identity provider.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in the product that ever sees a password, and it does not keep
/// one: the pool is configured for SRP, so the password is used to derive a proof and is
/// never transmitted, never stored, and never logged.
/// </para>
/// <para>
/// Every failure is reported the same way. Distinguishing "no such account" from "wrong
/// password" turns sign-in into an account-enumeration oracle, which is worth more to an
/// attacker than either message is worth to a user who mistyped something.
/// </para>
/// </remarks>
public interface IIdentityService
{
    /// <summary>Signs a user in, returning <c>null</c> when the credentials do not verify.</summary>
    Task<AuthenticatedSession?> SignInAsync(string email, string password, CancellationToken ct);

    /// <summary>Registers a new account.</summary>
    Task<RegistrationOutcome> RegisterAsync(
        string username, string email, string password, CancellationToken ct);

    /// <summary>Exchanges a refresh token for a fresh access token.</summary>
    Task<AuthenticatedSession?> RefreshAsync(string refreshToken, string username, CancellationToken ct);

    /// <summary>Revokes a refresh token so a signed-out session cannot be resumed.</summary>
    /// <remarks>
    /// Clearing the cookie alone would leave the refresh token valid for its full thirty
    /// days in the hands of anyone who captured it. Signing out has to mean something on
    /// the server as well as in the browser.
    /// </remarks>
    Task SignOutAsync(string refreshToken, CancellationToken ct);

    /// <summary>
    /// Submits the code emailed at registration, moving the account from unconfirmed to
    /// usable.
    /// </summary>
    /// <remarks>
    /// The pool is <c>autoVerify: {email:true}</c> — a self-signed-up account starts
    /// <c>UNCONFIRMED</c> and every sign-in attempt fails until this succeeds. There is no
    /// way to skip it against a real pool, so a frontend that never renders this step
    /// strands every real signup on an unconfirmed account it cannot explain.
    /// </remarks>
    Task<ConfirmOutcome> ConfirmSignUpAsync(string username, string code, CancellationToken ct);

    /// <summary>
    /// Asks the pool to re-send the confirmation code, when the first one expired or never
    /// arrived.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> for every failure — no such account, already confirmed, or the
    /// pool's own resend limit — for the same anti-enumeration reason sign-in gives one
    /// answer for every failure.
    /// </remarks>
    Task<bool> ResendConfirmationCodeAsync(string username, CancellationToken ct);

    /// <summary>
    /// Starts a passwordless sign-in: emails a one-time code and returns the opaque
    /// continuation token <see cref="VerifyEmailOtpAsync"/> needs to complete it.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> for every failure, including "no such account" — the same
    /// anti-enumeration reasoning as <see cref="SignInAsync"/>, and for the same practical
    /// reason: this is now the *primary* sign-in path, so it is exactly as attackable as
    /// password sign-in used to be alone.
    /// </remarks>
    Task<OtpChallenge?> RequestEmailOtpAsync(string email, CancellationToken ct);

    /// <summary>Completes a passwordless sign-in with the code the reader received.</summary>
    /// <remarks>
    /// Almost as coarse as every other method here — see <see cref="OtpVerifyOutcome"/> for
    /// the one exception this makes and why it is safe.
    /// </remarks>
    Task<OtpVerifyOutcome> VerifyEmailOtpAsync(
        string email, string session, string code, CancellationToken ct);

    /// <summary>
    /// Completes federated sign-in: trades the authorization code the hosted UI redirected
    /// back with for a session.
    /// </summary>
    /// <param name="code">The <c>code</c> query parameter the hosted UI redirected with.</param>
    /// <param name="redirectUri">
    /// Must exactly match the one used to start the authorize request — the token endpoint
    /// rejects the exchange otherwise, as a defence against a stolen code being redeemed
    /// from somewhere else.
    /// </param>
    /// <param name="codeVerifier">
    /// The PKCE verifier the caller generated before the redirect. Proves this exchange
    /// comes from whoever started the sign-in, not from someone who merely observed the
    /// authorization code in a referrer header or a browser history entry.
    /// </param>
    /// <remarks>
    /// Returns <c>null</c> for every failure, including "Google sign-in is not configured
    /// yet" — the caller cannot act differently on the distinction, and a coarse answer is
    /// consistent with every other method on this interface.
    /// </remarks>
    Task<AuthenticatedSession?> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken ct);

    /// <summary>
    /// Resolves the caller's own email address from their access token.
    /// </summary>
    /// <remarks>
    /// The access token carries <c>sub</c> and <c>client_id</c>, never an email — Cognito
    /// only puts that on the id token, which this API never accepts as a credential. This
    /// exists for the one place an endpoint legitimately needs it anyway: sending a
    /// verification email to the account's own address, e.g. when a profile has no phone
    /// number yet to fall back to. It authenticates with the caller's own bearer token
    /// (the same call <see cref="VerifyEmailOtpAsync"/> already makes to resolve a
    /// username), never with AWS credentials — this process still has no authority over
    /// the pool beyond what the caller's own token grants for themselves.
    /// </remarks>
    Task<string?> GetEmailAsync(string accessToken, CancellationToken ct);
}

/// <param name="AccessToken">The bearer token the API validates. Short-lived.</param>
/// <param name="RefreshToken">
/// Long-lived, and the more dangerous of the two. It never reaches client-side script:
/// the web tier keeps it in an HttpOnly cookie and exchanges it server-side.
/// </param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops being accepted.</param>
/// <param name="Username">The handle, which doubles as the shop name.</param>
public sealed record AuthenticatedSession(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string Username);

/// <param name="Succeeded">Whether the account now exists.</param>
/// <param name="NeedsConfirmation">Whether a verification code was sent to the address.</param>
/// <param name="FailureCode">
/// A stable code for the frontend to translate, or <c>null</c> on success. Deliberately
/// coarse: it says a password was too weak or a handle was taken, never whether an email
/// address is already registered.
/// </param>
/// <param name="DevCode">
/// The confirmation code itself — present only when the local development identity stub
/// issues this, for the same reason as <see cref="OtpChallenge.DevCode"/>: local development
/// has no mailbox to deliver it to. The production Cognito adapter never sets this; Cognito
/// emails the code and this process never sees it.
/// </param>
public sealed record RegistrationOutcome(
    bool Succeeded,
    bool NeedsConfirmation,
    string? FailureCode,
    string? DevCode = null)
{
    public static RegistrationOutcome Confirmed { get; } = new(true, false, null);

    public static RegistrationOutcome AwaitingConfirmation { get; } = new(true, true, null);

    /// <summary>Same as <see cref="AwaitingConfirmation"/>, carrying the code the stub issued.</summary>
    public static RegistrationOutcome AwaitingConfirmationWithCode(string devCode) => new(true, true, null, devCode);

    public static RegistrationOutcome Failed(string code) => new(false, false, code);
}

/// <param name="Succeeded">Whether the account is now confirmed and can sign in.</param>
/// <param name="FailureCode">
/// A stable code for the frontend to translate: the code was wrong, expired, or the
/// account is already confirmed. Never distinguishes "no such account" from these — that
/// would let this endpoint be used to test whether a username exists.
/// </param>
public sealed record ConfirmOutcome(bool Succeeded, string? FailureCode)
{
    public static ConfirmOutcome Success { get; } = new(true, null);

    public static ConfirmOutcome Failed(string code) => new(false, code);
}

/// <param name="Session">
/// Opaque. Carried by the client from request to verify and back to us; never inspected or
/// stored here, exactly like a CSRF token nobody but the issuer needs to understand.
/// </param>
/// <param name="DevCode">
/// The code itself — present only when the local development identity stub issues this,
/// because local development has no mailbox to deliver it to. The production Cognito
/// adapter never sets this; Cognito emails the code and this process never sees it.
/// </param>
public sealed record OtpChallenge(string Session, string? DevCode = null);

/// <param name="Session">The signed-in session — present only on success.</param>
/// <param name="UnconfirmedUsername">
/// Set only when the code and session both matched but the account never completed sign-up
/// confirmation. Naming the account here does not reopen the enumeration channel
/// <see cref="IIdentityService.RequestEmailOtpAsync"/>'s coarse response protects against:
/// reaching this branch already requires a code and session that matched, proof of mailbox
/// access a stranger probing this endpoint could not produce. Every other failure — wrong
/// code, wrong session, expired, no such account — still answers through
/// <see cref="Failed"/> alone, exactly as coarse as before. Carries the handle so the caller
/// can send the reader straight to finishing confirmation without a second lookup.
/// </param>
public sealed record OtpVerifyOutcome(AuthenticatedSession? Session, string? UnconfirmedUsername)
{
    public static OtpVerifyOutcome Failed { get; } = new(null, null);

    public static OtpVerifyOutcome NotConfirmed(string username) => new(null, username);

    public static OtpVerifyOutcome Success(AuthenticatedSession session) => new(session, null);
}
