using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Amazon.Extensions.CognitoAuthentication;
using Koleksi.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Identity;

/// <summary>
/// Signs users in against the Cognito user pool using SRP.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why SRP and not <c>USER_PASSWORD_AUTH</c>.</b> The simpler flow posts the password
/// to Cognito, which means it exists in plaintext in one more process, one more TLS
/// termination point, and potentially one more log. SRP proves knowledge of the password
/// without transmitting it. The user pool is configured to accept only SRP, so this is not
/// a preference that can be quietly downgraded by a change here.
/// </para>
/// <para>
/// <b>Why anonymous AWS credentials.</b> <c>InitiateAuth</c> and <c>SignUp</c> are
/// unauthenticated Cognito APIs. Calling them with the Lambda's execution role would work
/// and would also mean a compromise of this service inherited whatever else that role can
/// do to the pool. Anonymous credentials give the identity path no AWS authority at all.
/// </para>
/// </remarks>
internal sealed partial class CognitoIdentityService(
    IAmazonCognitoIdentityProvider provider,
    CognitoOptions options,
    IHttpClientFactory httpClientFactory,
    ILogger<CognitoIdentityService> logger) : IIdentityService
{
    /// <summary>
    /// Name of the <see cref="IHttpClientFactory"/> client used for the hosted UI's OAuth
    /// token endpoint.
    /// </summary>
    /// <remarks>
    /// A factory-managed client, not a field, even though this class is registered as a
    /// singleton: the factory is what pools and recycles the underlying connections
    /// correctly, which a single long-lived <see cref="HttpClient"/> field would not do
    /// across a Lambda's lifetime, and it is the pattern Microsoft's own guidance recommends
    /// for exactly this shape of singleton.
    /// </remarks>
    internal const string HttpClientName = "CognitoHostedUi";


    public async Task<AuthenticatedSession?> SignInAsync(
        string email, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var pool = new CognitoUserPool(options.UserPoolId, options.ClientId, provider);
        var user = new CognitoUser(email, options.ClientId, pool, provider);

        try
        {
            var result = await user
                .StartWithSrpAuthAsync(new InitiateSrpAuthRequest { Password = password }, ct)
                .ConfigureAwait(false);

            if (result.AuthenticationResult is not { } tokens)
            {
                // A challenge we do not implement — MFA, or a forced password reset. Not a
                // failure to report in detail; the caller sees the same "check your
                // credentials" as any other unsuccessful attempt.
                LogChallengeUnsupported(logger, result.ChallengeName?.Value ?? "unknown");
                return null;
            }

            return ToSession(tokens, user.Username);
        }
        catch (NotAuthorizedException)
        {
            // Wrong password, or an account that does not exist and the pool is answering
            // identically on purpose. Deliberately indistinguishable here too.
            return null;
        }
        catch (UserNotFoundException)
        {
            return null;
        }
        catch (UserNotConfirmedException)
        {
            return null;
        }
        catch (TooManyRequestsException)
        {
            // Cognito's own throttle, on top of ours. Surfaced as a failure rather than an
            // error so a burst of guesses learns nothing from the difference.
            LogThrottled(logger);
            return null;
        }
    }

    public async Task<RegistrationOutcome> RegisterAsync(
        string username, string email, string password, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        try
        {
            var response = await provider.SignUpAsync(
                new SignUpRequest
                {
                    ClientId = options.ClientId,
                    Username = username,
                    Password = password,
                    UserAttributes =
                    [
                        new AttributeType { Name = "email", Value = email },
                    ],
                },
                ct).ConfigureAwait(false);

            // Nullable in the SDK. Treated as "not confirmed" when absent, because telling
            // someone their account is ready when it is not sends them to a sign-in that
            // will refuse them with no explanation.
            return response.UserConfirmed == true
                ? RegistrationOutcome.Confirmed
                : RegistrationOutcome.AwaitingConfirmation;
        }
        catch (UsernameExistsException)
        {
            // A handle is public — it is the shop name — so saying it is taken reveals
            // nothing that browsing to /toko/{handle} would not.
            return RegistrationOutcome.Failed("auth.username_taken");
        }
        catch (AliasExistsException)
        {
            // Thrown when the email — configured as a sign-in alias — already belongs to a
            // different account. Answered with a code distinct from auth.username_taken:
            // the username just cleared SignUp's own uniqueness check, so telling the caller
            // their username is taken would be false. This code stays generic enough that it
            // does not confirm the email itself is the registered one.
            return RegistrationOutcome.Failed("auth.registration_failed");
        }
        catch (InvalidPasswordException)
        {
            return RegistrationOutcome.Failed("auth.password_too_weak");
        }
        catch (InvalidParameterException)
        {
            return RegistrationOutcome.Failed("auth.invalid_details");
        }
        catch (TooManyRequestsException)
        {
            LogThrottled(logger);
            return RegistrationOutcome.Failed("auth.try_again_later");
        }
    }

    public async Task<AuthenticatedSession?> RefreshAsync(
        string refreshToken, string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        try
        {
            var response = await provider.InitiateAuthAsync(
                new InitiateAuthRequest
                {
                    ClientId = options.ClientId,
                    AuthFlow = AuthFlowType.REFRESH_TOKEN_AUTH,
                    AuthParameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["REFRESH_TOKEN"] = refreshToken,
                    },
                },
                ct).ConfigureAwait(false);

            return response.AuthenticationResult is { } tokens ? ToSession(tokens, username) : null;
        }
        catch (NotAuthorizedException)
        {
            // Revoked, expired, or never valid. The caller signs the session out.
            return null;
        }
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        try
        {
            await provider.RevokeTokenAsync(
                new RevokeTokenRequest { ClientId = options.ClientId, Token = refreshToken },
                ct).ConfigureAwait(false);
        }
        catch (NotAuthorizedException)
        {
            // Already revoked. Signing out twice is not an error worth surfacing.
        }
    }

    public async Task<ConfirmOutcome> ConfirmSignUpAsync(string username, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        try
        {
            await provider.ConfirmSignUpAsync(
                new ConfirmSignUpRequest { ClientId = options.ClientId, Username = username, ConfirmationCode = code },
                ct).ConfigureAwait(false);

            return ConfirmOutcome.Success;
        }
        catch (CodeMismatchException)
        {
            return ConfirmOutcome.Failed("auth.code_invalid");
        }
        catch (ExpiredCodeException)
        {
            return ConfirmOutcome.Failed("auth.code_expired");
        }
        catch (NotAuthorizedException)
        {
            // Cognito's own answer when the account is already confirmed.
            return ConfirmOutcome.Failed("auth.already_confirmed");
        }
        catch (UserNotFoundException)
        {
            // Same coarse code a wrong code gets — confirming or denying the username
            // exists is exactly the oracle sign-in already refuses to be.
            return ConfirmOutcome.Failed("auth.code_invalid");
        }
        catch (TooManyRequestsException)
        {
            LogThrottled(logger);
            return ConfirmOutcome.Failed("auth.try_again_later");
        }
    }

    public async Task<bool> ResendConfirmationCodeAsync(string username, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        try
        {
            await provider.ResendConfirmationCodeAsync(
                new ResendConfirmationCodeRequest { ClientId = options.ClientId, Username = username },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (UserNotFoundException)
        {
            return false;
        }
        catch (InvalidParameterException)
        {
            // The pool's own answer when the account is already confirmed — nothing to
            // resend a code for.
            return false;
        }
        catch (LimitExceededException)
        {
            return false;
        }
        catch (TooManyRequestsException)
        {
            LogThrottled(logger);
            return false;
        }
    }

    public async Task<OtpChallenge?> RequestEmailOtpAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        try
        {
            var response = await provider.InitiateAuthAsync(
                new InitiateAuthRequest
                {
                    ClientId = options.ClientId,
                    AuthFlow = AuthFlowType.USER_AUTH,
                    AuthParameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["USERNAME"] = email,
                        ["PREFERRED_CHALLENGE"] = "EMAIL_OTP",
                    },
                },
                ct).ConfigureAwait(false);

            if (response.ChallengeName != ChallengeNameType.EMAIL_OTP || response.Session is null)
            {
                // An account not eligible for email-OTP, or the pool answering with
                // something this flow does not implement. Reported the same coarse way as
                // any other sign-in failure — see the class remarks.
                LogChallengeUnsupported(logger, response.ChallengeName?.Value ?? "unknown");
                return null;
            }

            return new OtpChallenge(response.Session);
        }
        catch (UserNotFoundException)
        {
            // The client's own `preventUserExistenceErrors` setting is meant to keep this
            // unreachable, but USER_AUTH is different enough from the flows that setting
            // was written for that this is handled defensively rather than assumed covered.
            return null;
        }
        catch (NotAuthorizedException)
        {
            return null;
        }
        catch (UserNotConfirmedException)
        {
            // Answered the same coarse way as every other failure here — unlike
            // VerifyEmailOtpAsync's equivalent catch, nothing has proven mailbox access at
            // this point, only that an email was typed in. Naming the account as
            // unconfirmed here would be exactly the oracle this method's remarks describe.
            return null;
        }
        catch (TooManyRequestsException)
        {
            LogThrottled(logger);
            return null;
        }
    }

    public async Task<OtpVerifyOutcome> VerifyEmailOtpAsync(
        string email, string session, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        try
        {
            var response = await provider.RespondToAuthChallengeAsync(
                new RespondToAuthChallengeRequest
                {
                    ClientId = options.ClientId,
                    ChallengeName = ChallengeNameType.EMAIL_OTP,
                    Session = session,
                    ChallengeResponses = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["USERNAME"] = email,
                        ["EMAIL_OTP_CODE"] = code,
                    },
                },
                ct).ConfigureAwait(false);

            if (response.AuthenticationResult is not { } tokens)
            {
                return OtpVerifyOutcome.Failed;
            }

            /*
             * The handle, not the email, is what `AuthenticatedSession.Username` carries
             * everywhere else — it is the shop name. This flow signs in with an email, and
             * neither the challenge response nor the tokens carry the resolved handle back
             * to us, so it is looked up with the access token this response just minted.
             * `GetUser` authenticates with the bearer token itself, not AWS credentials,
             * keeping this on the same "no AWS authority" footing as the rest of the class.
             */
            var user = await provider
                .GetUserAsync(new GetUserRequest { AccessToken = tokens.AccessToken }, ct)
                .ConfigureAwait(false);

            return OtpVerifyOutcome.Success(ToSession(tokens, user.Username));
        }
        catch (CodeMismatchException)
        {
            return OtpVerifyOutcome.Failed;
        }
        catch (ExpiredCodeException)
        {
            return OtpVerifyOutcome.Failed;
        }
        catch (UserNotConfirmedException)
        {
            /*
             * Reaching a real challenge response at all already required a code and session
             * that matched what the pool issued, so — same reasoning as
             * LocalIdentityService's equivalent branch — this does not reopen an enumeration
             * channel. It cannot name the account the way the local stub does, though:
             * unlike RespondToAuthChallenge's success path, this exception carries no tokens
             * to resolve a handle from, and looking one up any other way would need AWS
             * authority this class deliberately does not have. Falling back to the email is
             * still enough for the frontend to send the reader to finish confirming.
             */
            return OtpVerifyOutcome.NotConfirmed(email);
        }
        catch (NotAuthorizedException)
        {
            return OtpVerifyOutcome.Failed;
        }
        catch (TooManyRequestsException)
        {
            LogThrottled(logger);
            return OtpVerifyOutcome.Failed;
        }
    }

    public async Task<AuthenticatedSession?> ExchangeAuthorizationCodeAsync(
        string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        if (string.IsNullOrWhiteSpace(options.HostedUiDomain))
        {
            // Google sign-in is not configured on this deployment yet — see
            // CognitoOptions.HostedUiDomain. Nothing to exchange against.
            return null;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.PostAsync(
                $"{options.HostedUiDomain}/oauth2/token",
                new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "authorization_code",

                    // No client secret: the web client is public (generateSecret: false on
                    // the CDK client), so PKCE's code_verifier is what proves this exchange
                    // belongs to the request that started it.
                    ["client_id"] = options.ClientId,
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri,
                    ["code_verifier"] = codeVerifier,
                }),
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // An expired or already-used code, a redirect_uri mismatch, or a verifier
                // that does not match the challenge. Reported the same coarse way as any
                // other sign-in failure rather than surfacing the token endpoint's reason.
                LogHostedUiExchangeFailed(logger, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<TokenResponse>(ct)
                .ConfigureAwait(false);

            if (payload?.AccessToken is not { Length: > 0 } accessToken)
            {
                return null;
            }

            // Same reasoning as VerifyEmailOtpAsync above: the token response carries no
            // resolved username/handle, so it is looked up with the access token this
            // response just minted, keeping this on the same "no AWS credentials" footing.
            var user = await provider
                .GetUserAsync(new GetUserRequest { AccessToken = accessToken }, ct)
                .ConfigureAwait(false);

            return new AuthenticatedSession(
                accessToken,
                payload.RefreshToken,
                DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn ?? 3600),
                user.Username);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // The client's own timeout, not the caller's cancellation.
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            // The token endpoint answered 2xx with a body this shape does not understand —
            // a contract change, not an attacker. Refused rather than guessed at, same as
            // PaymentGateway.VerifyAndParseWebhook's unreadable-payload path.
            return null;
        }
    }

    /// <summary>The subset of the token endpoint's response this exchange needs.</summary>
    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    public async Task<string?> GetEmailAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        try
        {
            var user = await provider
                .GetUserAsync(new GetUserRequest { AccessToken = accessToken }, ct)
                .ConfigureAwait(false);

            return user.UserAttributes
                .FirstOrDefault(attribute => string.Equals(attribute.Name, "email", StringComparison.Ordinal))
                ?.Value;
        }
        catch (NotAuthorizedException)
        {
            // An expired or revoked token. The caller has nothing to email regardless.
            return null;
        }
    }

    /// <summary>
    /// Maps Cognito's result onto our own session record.
    /// </summary>
    /// <remarks>
    /// The id token is dropped rather than passed on. It describes the user to a client
    /// and is not a credential this API accepts, so carrying it further would only create
    /// somewhere else for it to leak from.
    /// </remarks>
    private static AuthenticatedSession ToSession(AuthenticationResultType tokens, string username) =>
        new(
            tokens.AccessToken,
            tokens.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600),
            username);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Sign-in returned an unsupported challenge: {Challenge}.")]
    private static partial void LogChallengeUnsupported(ILogger logger, string challenge);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "Cognito throttled an identity request.")]
    private static partial void LogThrottled(ILogger logger);

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "The hosted UI's token endpoint refused an authorization-code exchange: HTTP {StatusCode}.")]
    private static partial void LogHostedUiExchangeFailed(ILogger logger, int statusCode);
}
