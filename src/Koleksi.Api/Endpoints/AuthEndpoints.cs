using Koleksi.Application.Abstractions;
using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Sign-in, sign-up, refresh and sign-out.
/// </summary>
/// <remarks>
/// <para>
/// The only anonymous <b>write</b> surface in the product, which is what makes it the most
/// attacked one. Three controls apply and each does a different job: a rate-limit bucket
/// far tighter than anything else, keyed on the caller's address because there is by
/// definition no identity yet; a single opaque failure for every unsuccessful sign-in, so
/// the endpoint cannot be used to enumerate accounts; and a response that carries the
/// refresh token to the web tier and nowhere else.
/// </para>
/// <para>
/// Passwords are read from the body and handed straight to the identity service. They are
/// never logged, never stored, never placed on a query string, and — because the pool is
/// SRP-only — never transmitted to Cognito either.
/// </para>
/// </remarks>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/auth")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithTags("Auth");

        group.MapPost("/login", SignInAsync).WithName("SignIn");
        group.MapPost("/register", RegisterAsync).WithName("Register");
        group.MapPost("/refresh", RefreshAsync).WithName("RefreshSession");
        group.MapPost("/logout", SignOutAsync).WithName("SignOut");
        group.MapPost("/confirm", ConfirmAsync).WithName("ConfirmSignUp");
        group.MapPost("/confirm/resend", ResendConfirmationAsync).WithName("ResendConfirmation");
        group.MapPost("/otp/request", RequestOtpAsync).WithName("RequestEmailOtp");
        group.MapPost("/otp/verify", VerifyOtpAsync).WithName("VerifyEmailOtp");
        group.MapPost("/google/exchange", ExchangeGoogleAsync).WithName("ExchangeGoogleCode");

        return app;
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return InvalidCredentials();
        }

        var session = await identity
            .SignInAsync(request.Email.Trim(), request.Password, ct)
            .ConfigureAwait(false);

        // One answer for every kind of failure: no such account, wrong password, account
        // not confirmed, Cognito throttling us. Anything more specific is an oracle.
        return session is null ? InvalidCredentials() : Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RequestOtpAsync(
        RequestOtpRequest request, IIdentityService identity, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email))
        {
            return InvalidCredentials();
        }

        // Short retention, same reasoning as the confirmation-code resend: long enough to
        // absorb a double-tap on "Send code", short enough that a reader who genuinely
        // wants a second email a minute later is not silently ignored.
        var challenge = await idempotency.RunOnceAsync(
            "auth.otp-request",
            Read(http),
            TimeSpan.FromSeconds(30),
            () => identity.RequestEmailOtpAsync(request.Email.Trim(), ct),
            ct).ConfigureAwait(false);

        // No InvalidCredentials() here: a session token is always returned, even for an
        // address with no account, so the response cannot be used to test whether one
        // exists. See IIdentityService.RequestEmailOtpAsync and both implementations.
        return challenge is null
            ? InvalidCredentials()
            : Results.Ok(new { challenge.Session, challenge.DevCode });
    }

    private static async Task<IResult> VerifyOtpAsync(
        VerifyOtpRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Session)
            || string.IsNullOrWhiteSpace(request.Code))
        {
            return InvalidCredentials();
        }

        // Deliberately not idempotency-wrapped: an OTP code is already single-use (the
        // local stub removes its state on first use; Cognito's session/challenge is
        // consumed the same way), so a genuine replay of the same code and session is
        // naturally answered "no" the second time regardless, and wrapping it would only
        // teach a guesser that repeating a wrong code with the same key gets the same
        // wrong answer for free instead of spending another attempt against the rate limit.
        var outcome = await identity
            .VerifyEmailOtpAsync(request.Email.Trim(), request.Session, request.Code.Trim(), ct)
            .ConfigureAwait(false);

        if (outcome.Session is { } session)
        {
            return Results.Ok(ToResponse(session));
        }

        // See OtpVerifyOutcome.UnconfirmedUsername: reaching this branch already required a
        // code and session that matched, so naming the account is safe here in a way it is
        // not for the plain "no match" answer below.
        if (outcome.UnconfirmedUsername is { } username)
        {
            return Results.Json(
                new { code = "auth.otp_account_not_confirmed", username },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return InvalidCredentials();
    }

    private static async Task<IResult> ExchangeGoogleAsync(
        GoogleExchangeRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.RedirectUri)
            || string.IsNullOrWhiteSpace(request.CodeVerifier))
        {
            return InvalidCredentials();
        }

        // Deliberately not idempotency-wrapped, for the same reason as /otp/verify: an
        // authorization code is already single-use at the token endpoint, so a genuine
        // replay fails there regardless of any key this process could add.
        var session = await identity
            .ExchangeAuthorizationCodeAsync(request.Code, request.RedirectUri, request.CodeVerifier, ct)
            .ConfigureAwait(false);

        return session is null ? InvalidCredentials() : Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request, IIdentityService identity, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new { code = "auth.invalid_details" });
        }

        // A double-tap on "Create account" — or a browser retrying a POST on a flaky
        // connection — must not attempt to create the same account twice. Cognito would
        // answer the second attempt with "username taken" regardless, but that reads to
        // the person who single-clicked as a real failure rather than the harmless replay
        // it actually was.
        var outcome = await idempotency.RunOnceAsync(
            "auth.register",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => identity.RegisterAsync(request.Username.Trim(), request.Email.Trim(), request.Password, ct),
            ct).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return Results.BadRequest(new { code = outcome.FailureCode });
        }

        // No tokens here even when the pool auto-confirms. Signing in is a separate,
        // separately rate-limited step, so a registration flood cannot mint sessions.
        return Results.Ok(new { needsConfirmation = outcome.NeedsConfirmation, devCode = outcome.DevCode });
    }

    private static async Task<IResult> ConfirmAsync(
        ConfirmRequest request, IIdentityService identity, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(new { code = "auth.code_invalid" });
        }

        var outcome = await idempotency.RunOnceAsync(
            "auth.confirm",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => identity.ConfirmSignUpAsync(request.Username.Trim(), request.Code.Trim(), ct),
            ct).ConfigureAwait(false);

        return outcome.Succeeded ? Results.Ok() : Results.BadRequest(new { code = outcome.FailureCode });
    }

    private static async Task<IResult> ResendConfirmationAsync(
        ResendConfirmationRequest request, IIdentityService identity, IIdempotencyStore idempotency, HttpRequest http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Results.Ok();
        }

        // Short retention on purpose — long enough that a double-tap on "Resend" shares one
        // claim, short enough that a reader who genuinely wants a second code a minute
        // later is not silently ignored by a claim from their first request.
        await idempotency.RunOnceAsync(
            "auth.confirm-resend",
            Read(http),
            TimeSpan.FromSeconds(30),
            () => identity.ResendConfirmationCodeAsync(request.Username.Trim(), ct),
            ct).ConfigureAwait(false);

        // One answer regardless of outcome — same reasoning as sign-in: whether resending
        // succeeded reveals whether the account exists and whether it is already confirmed.
        return Results.Ok();
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.RefreshToken) || string.IsNullOrWhiteSpace(request.Username))
        {
            return InvalidCredentials();
        }

        var session = await identity
            .RefreshAsync(request.RefreshToken, request.Username, ct)
            .ConfigureAwait(false);

        return session is null ? InvalidCredentials() : Results.Ok(ToResponse(session));
    }

    private static async Task<IResult> SignOutAsync(
        RefreshRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            // Revoked server-side, not merely forgotten client-side. Clearing a cookie
            // leaves the refresh token usable for its full thirty days by anyone holding a
            // copy of it.
            await identity.SignOutAsync(request.RefreshToken, ct).ConfigureAwait(false);
        }

        return Results.NoContent();
    }

    private static IResult InvalidCredentials() => Results.Json(
        new { code = "auth.invalid_credentials" },
        statusCode: StatusCodes.Status401Unauthorized);

    private static object ToResponse(AuthenticatedSession session) => new
    {
        session.AccessToken,
        session.RefreshToken,
        session.ExpiresAt,
        session.Username,
    };

    /// <param name="Email">The address the account was registered with.</param>
    /// <param name="Password">
    /// Used to derive an SRP proof and then discarded. It is not persisted anywhere in
    /// this process and is not forwarded to Cognito.
    /// </param>
    private sealed record SignInRequest(string Email, string Password);

    /// <param name="Username">The handle, which becomes the public shop name.</param>
    private sealed record RegisterRequest(string Username, string Email, string Password);

    /// <param name="Username">Required by Cognito to compute the refresh exchange.</param>
    private sealed record RefreshRequest(string RefreshToken, string Username);

    private sealed record ConfirmRequest(string Username, string Code);

    private sealed record ResendConfirmationRequest(string Username);

    private sealed record RequestOtpRequest(string Email);

    /// <param name="Session">The opaque token <c>/otp/request</c> returned.</param>
    private sealed record VerifyOtpRequest(string Email, string Session, string Code);

    /// <param name="RedirectUri">Must match the one used to start the authorize request.</param>
    /// <param name="CodeVerifier">The PKCE verifier generated before the redirect.</param>
    private sealed record GoogleExchangeRequest(string Code, string RedirectUri, string CodeVerifier);
}
