#if DEBUG
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Koleksi.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Koleksi.AdminApi.Development;

/// <summary>
/// A one-account staff pool that lives in this process, for working on the admin site on a developer's machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The real staff pool needs MFA and exists only in a deployed stage, and the admin stack cannot be
/// deployed until the owner supplies an IP allowlist. Without a stand-in nobody could sign in to the admin site to build or
/// test any screen behind the sign-in. This mirrors what <c>LocalIdentity</c> does for the public API.
/// </para>
/// <para>
/// <b>Why it cannot reach a deployed stage: four locks, each one checked in code.</b>
/// </para>
/// <list type="number">
///   <item><c>#if DEBUG</c> keeps it out of the compiled artefact. The Lambda publish is <c>-c Release</c>, so no
///   deployed binary contains this type at all. <c>scripts/verify-release-has-no-local-staff.ps1</c> publishes Release
///   and searches the output for this type's name.</item>
///   <item>The host environment must be Development.</item>
///   <item>The pool ids must still be the shipped placeholders, so configuring a real pool switches it off without anyone
///   having to know this file exists.</item>
///   <item><see cref="Use"/> throws when <c>KoleksiAdmin:Stage</c> is set, which only CDK ever sets, so even a Debug build
///   somebody deploys by mistake refuses to start with it.</item>
/// </list>
/// <para>
/// <b>What stays real.</b> The tokens are genuine RS256 JWTs in the shape Cognito issues, verified by the same JwtBearer
/// handler under the same issuer, <c>client_id</c>, <c>token_use</c>, algorithm and clock-skew rules as production. Only the
/// signing key differs. There is no MFA here, and that is the one thing it cannot rehearse.
/// </para>
/// </remarks>
internal static partial class LocalStaffIdentity
{
    private const string Placeholder = "REPLACEME";

    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(8);

    public static bool IsUnconfigured(KoleksiAdminOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.StaffPool.UserPoolId.Contains(Placeholder, StringComparison.OrdinalIgnoreCase)
            || settings.StaffPool.ClientId.Contains(Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    public static void Use(IServiceCollection services, KoleksiAdminOptions settings, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configuration);

        if (settings.IsDeployed)
        {
            throw new InvalidOperationException(
                "The local staff identity is a development stand-in and must never run on a deployed stage "
                + $"(KoleksiAdmin:Stage is '{settings.Stage}'). Deploy a Release build with real staff pool settings.");
        }

        var username = configuration["KoleksiAdmin:LocalStaff:Username"];
        var password = configuration["KoleksiAdmin:LocalStaff:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "KoleksiAdmin:LocalStaff:Username (appsettings.Development.json) and Password are required for the local staff identity. "
                + "The password is not in any file: set it once with "
                + "dotnet user-secrets set \"KoleksiAdmin:LocalStaff:Password\" \"<a long random value>\" --project src/Koleksi.AdminApi");
        }

        // A fresh key per process: restarting the API signs every local session out, as it does for the public API.
        var signingKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "koleksi-admin-local-dev" };
        var accounts = new LocalStaffAccounts(
            new LocalStaffAccount(StableSubject(username), username, password),
            signingKey,
            settings.StaffPool.Authority,
            settings.StaffPool.ClientId);
        services.AddSingleton(accounts);

        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = settings.StaffPool.Authority };
            metadata.SigningKeys.Add(signingKey);

            options.Configuration = metadata;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            options.TokenValidationParameters.IssuerSigningKey = signingKey;
        });
    }

    public static void MapEndpoints(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        DevDisputeFixture.Map(app.MapGroup("/dev"));

        var group = app.MapGroup("/dev/staff").AllowAnonymous();

        group.MapPost("/sign-in", (LocalStaffSignIn body, LocalStaffAccounts accounts) =>
            accounts.SignIn(body.Username, body.Password) is { } session
                ? Results.Ok(session)
                : Results.Json(new { code = "auth.invalid_credentials" }, statusCode: StatusCodes.Status401Unauthorized));

        group.MapPost("/refresh", (LocalStaffRefresh body, LocalStaffAccounts accounts) =>
            accounts.Refresh(body.RefreshToken) is { } session
                ? Results.Ok(session)
                : Results.Json(new { code = "auth.session_expired" }, statusCode: StatusCodes.Status401Unauthorized));

        group.MapPost("/sign-out", (LocalStaffRefresh body, LocalStaffAccounts accounts) =>
        {
            accounts.SignOut(body.RefreshToken);
            return Results.NoContent();
        });
    }

    /// <summary>A stable id for the local account, so audit rows written on one run still name the same person on the next.</summary>
    private static string StableSubject(string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("koleksi-admin-local:" + username));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "LOCAL STAFF IDENTITY IS ACTIVE. One development account, tokens signed by this process, no MFA. "
            + "Reached only by a Debug build, in the Development environment, with placeholder staff pool ids and no stage name.")]
    public static partial void LogActive(ILogger logger);

    internal sealed record LocalStaffAccount(string Subject, string Username, string Password);

    internal sealed record LocalStaffSignIn(string Username, string Password);

    internal sealed record LocalStaffRefresh(string RefreshToken);

    internal sealed record LocalStaffSession(
        string AccessToken, string RefreshToken, int ExpiresInSeconds, string StaffId, string Username);

    internal sealed class LocalStaffAccounts(
        LocalStaffAccount account, RsaSecurityKey signingKey, string issuer, string clientId)
    {
        private static readonly JsonWebTokenHandler Handler = new();

        private readonly ConcurrentDictionary<string, DateTimeOffset> refreshTokens = new(StringComparer.Ordinal);

        public LocalStaffSession? SignIn(string? username, string? password)
        {
            // One answer for every failure, and the password compared as hashes in fixed time.
            var userMatches = string.Equals(username?.Trim(), account.Username, StringComparison.OrdinalIgnoreCase);
            var passwordMatches = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty)),
                SHA256.HashData(Encoding.UTF8.GetBytes(account.Password)));

            return userMatches && passwordMatches ? Issue(refreshToken: null) : null;
        }

        public LocalStaffSession? Refresh(string? refreshToken) =>
            refreshToken is not null
            && refreshTokens.TryGetValue(refreshToken, out var expires)
            && expires > DateTimeOffset.UtcNow
                ? Issue(refreshToken)
                : null;

        public void SignOut(string? refreshToken)
        {
            if (refreshToken is not null)
            {
                refreshTokens.TryRemove(refreshToken, out _);
            }
        }

        private LocalStaffSession Issue(string? refreshToken)
        {
            var now = DateTimeOffset.UtcNow;
            var token = Handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                IssuedAt = now.UtcDateTime,
                NotBefore = now.UtcDateTime,
                Expires = (now + AccessTokenLifetime).UtcDateTime,
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
                Claims = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["sub"] = account.Subject,
                    ["username"] = account.Username,
                    ["client_id"] = clientId,
                    ["token_use"] = "access",
                    ["jti"] = Guid.NewGuid().ToString("N"),
                },
            });

            if (refreshToken is null)
            {
                refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                refreshTokens[refreshToken] = now + RefreshTokenLifetime;
            }

            return new LocalStaffSession(token, refreshToken, (int)AccessTokenLifetime.TotalSeconds, account.Subject, account.Username);
        }
    }
}
#endif
