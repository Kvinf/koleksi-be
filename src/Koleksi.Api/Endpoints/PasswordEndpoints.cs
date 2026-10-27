using Koleksi.Application.Abstractions;
using Microsoft.AspNetCore.RateLimiting;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Forgotten-password reset (anonymous) and changing your own password (signed in).
/// </summary>
/// <remarks>
/// <para>
/// <b>The reset pair is anonymous, so it sits in the same bucket as sign-in.</b> Both routes
/// use <see cref="RateLimitPolicies.Authentication"/>: one pool of attempts per caller
/// address across login, register, OTP and these, so a guesser cannot get a fresh allowance
/// by switching to the reset form.
/// </para>
/// <para>
/// <b>Forgot never says whether the address is registered.</b> It answers the same <c>200</c>
/// and the same body for a registered address, an unknown one, an unconfirmed account and a
/// Google-only account. The only field that can differ is <c>devCode</c>, and that exists
/// only while the local development stub (compiled out of Release) is the identity service —
/// see <see cref="IIdentityService.RequestPasswordResetAsync"/>.
/// </para>
/// <para>
/// <b>Reset never separates "no such account" from "wrong code".</b> Both are
/// <c>auth.code_invalid</c>. The remaining codes (<c>auth.code_expired</c>,
/// <c>auth.password_too_weak</c>, <c>auth.try_again_later</c>) are things only someone holding
/// the code can reach, or that say nothing about any account.
/// </para>
/// <para>
/// <b>Change is the signed-in counterpart</b>, with its own per-user bucket
/// (<see cref="RateLimitPolicies.PasswordChange"/>): the caller has a valid token, so the
/// attack is someone holding a stolen token guessing the current password, and the budget
/// that matters is per account, not per address.
/// </para>
/// <para>
/// Passwords are read from the body and handed straight to the identity service: never
/// logged, never stored here, never echoed.
/// </para>
/// </remarks>
internal static class PasswordEndpoints
{
    /// <summary>Longest address RFC 5321 allows; anything longer cannot be a real mailbox.</summary>
    private const int MaxEmailLength = 254;

    /// <summary>Matches <c>passwordPolicy.minLength</c> in <c>auth-stack.ts</c>; change both.</summary>
    private const int MinPasswordLength = 8;

    /// <summary>The longest password Cognito accepts at all.</summary>
    private const int MaxPasswordLength = 256;

    public static IEndpointRouteBuilder MapPasswordEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var anonymous = app.MapGroup("/api/auth/password")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithTags("Auth");

        anonymous.MapPost("/forgot", ForgotAsync).WithName("ForgotPassword");
        anonymous.MapPost("/reset", ResetAsync).WithName("ResetPassword");

        // No AllowAnonymous: the fallback policy requires a valid access token.
        var signedIn = app.MapGroup("/api/auth/password").WithTags("Auth");

        signedIn.MapPost("/change", ChangeAsync)
            .RequireRateLimiting(RateLimitPolicies.PasswordChange)
            .WithName("ChangePassword");
        signedIn.MapGet("/status", StatusAsync).WithName("PasswordStatus");

        return app;
    }

    private static async Task<IResult> ForgotAsync(
        ForgotPasswordRequest request,
        IIdentityService identity,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Email?.Trim();

        // A blank or absurd address cannot be an account, so there is nothing to look up —
        // and nothing to hide, since the answer is the one every other address gets.
        string? devCode = null;
        if (!string.IsNullOrEmpty(email) && email.Length <= MaxEmailLength)
        {
            // Short retention, same reasoning as the OTP request: absorbs a double-tap on
            // "Send code" without swallowing a deliberate second request a minute later.
            devCode = await idempotency.RunOnceAsync(
                "auth.password-forgot",
                IdempotencyHeader.Read(http),
                TimeSpan.FromSeconds(30),
                () => identity.RequestPasswordResetAsync(email, ct),
                ct).ConfigureAwait(false);
        }

        return Results.Ok(new { accepted = true, devCode });
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest request, IIdentityService identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Email?.Trim();
        var code = request.Code?.Trim();

        if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength || string.IsNullOrEmpty(code))
        {
            return Failure("auth.code_invalid");
        }

        if (!PasswordLengthAcceptable(request.NewPassword))
        {
            return Failure("auth.password_too_weak");
        }

        // Deliberately not idempotency-wrapped, for the reason /otp/verify is not: the code
        // is single use, so a genuine replay is answered "no" the second time regardless, and
        // wrapping would only let a guesser repeat a wrong code for free.
        var outcome = await identity
            .ConfirmPasswordResetAsync(email, code, request.NewPassword!, ct)
            .ConfigureAwait(false);

        return outcome.Succeeded ? Results.Ok() : Failure(outcome.FailureCode);
    }

    private static async Task<IResult> ChangeAsync(
        ChangePasswordRequest request,
        IIdentityService identity,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Cannot happen for a caller who got past the fallback policy; a coarse refusal is
        // cheaper than a null dereference.
        if (BearerToken.Read(http) is not { Length: > 0 } accessToken)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > MaxPasswordLength)
        {
            return Failure("auth.current_password_invalid");
        }

        if (!PasswordLengthAcceptable(request.NewPassword))
        {
            return Failure("auth.password_too_weak");
        }

        // A double-tap on "Change password" must not run the change twice: the second call
        // would present a current password that no longer works and read as a failure to
        // someone whose first click succeeded. The recorded result holds no password.
        var outcome = await idempotency.RunOnceAsync(
            "auth.password-change",
            IdempotencyHeader.Read(http),
            TimeSpan.FromMinutes(2),
            () => identity.ChangePasswordAsync(accessToken, request.CurrentPassword, request.NewPassword!, ct),
            ct).ConfigureAwait(false);

        return outcome.Succeeded ? Results.NoContent() : Failure(outcome.FailureCode);
    }

    private static async Task<IResult> StatusAsync(
        IIdentityService identity, HttpRequest http, CancellationToken ct)
    {
        if (BearerToken.Read(http) is not { Length: > 0 } accessToken)
        {
            return Results.Unauthorized();
        }

        var canChange = await identity.CanChangePasswordAsync(accessToken, ct).ConfigureAwait(false);
        return Results.Ok(new { canChange });
    }

    private static bool PasswordLengthAcceptable(string? password) =>
        password is { Length: >= MinPasswordLength and <= MaxPasswordLength };

    private static IResult Failure(string? code) => Results.BadRequest(new { code = code ?? "auth.invalid_details" });

    /// <param name="Email">The address to send a reset code to.</param>
    private sealed record ForgotPasswordRequest(string? Email);

    /// <param name="Email">The address the code was requested for.</param>
    /// <param name="Code">The one-time code from the email.</param>
    /// <param name="NewPassword">Handed to the identity service and discarded.</param>
    private sealed record ResetPasswordRequest(string? Email, string? Code, string? NewPassword);

    private sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
}
