#if DEBUG
using System.Security.Cryptography;
using Amazon.DynamoDBv2;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Koleksi.Api.Development;

/// <summary>
/// A user pool that lives in this process, for local development only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>appsettings.Development.json</c> ships placeholder pool ids,
/// so out of the box every sign-in fails with "user pool client REPLACEME does not exist"
/// and nothing behind a login can be worked on without an AWS account. DynamoDB Local
/// already stands in for the table on the same principle; this stands in for the pool.
/// </para>
/// <para>
/// <b>Three locks, and what each one is for.</b> This is an authentication bypass — a type
/// that mints tokens the API accepts — so it is gated on all of:
/// </para>
/// <list type="number">
///   <item><c>#if DEBUG</c>, which keeps it out of the compiled artefact entirely. The
///   Lambda publish is <c>-c Release</c>, so no deployed binary contains this code: the
///   guarantee is the compiler's rather than a reviewer's.</item>
///   <item><c>IHostEnvironment.IsDevelopment()</c>, so a Debug build running as any other
///   environment uses Cognito — including the test host, which runs as <c>Testing</c>
///   precisely because these placeholder ids exist.</item>
///   <item>The pool ids still being placeholders, so the moment someone configures a real
///   pool they get the real provider without having to know this file exists.</item>
/// </list>
/// <para>
/// <b>What stays real.</b> Tokens are genuine RS256 JWTs with Cognito's claim shape,
/// verified by the same <c>JwtBearer</c> handler under the same validation parameters —
/// issuer, <c>client_id</c>, <c>token_use</c>, algorithm pinning, clock skew. Only the
/// signing key is swapped, exactly as the integration tests swap it. A local mode that
/// stubbed the authentication handler instead would prove that the stub works and would
/// hide every mistake in the configuration that actually ships.
/// </para>
/// </remarks>
internal static partial class LocalIdentity
{
    /// <summary>The value <c>appsettings.Development.json</c> ships in place of real ids.</summary>
    private const string Placeholder = "REPLACEME";

    /// <summary>Whether the configured pool is still the shipped placeholder.</summary>
    public static bool IsUnconfigured(CognitoOptions cognito)
    {
        ArgumentNullException.ThrowIfNull(cognito);

        return cognito.UserPoolId.Contains(Placeholder, StringComparison.OrdinalIgnoreCase)
            || cognito.ClientId.Contains(Placeholder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the Cognito adapter, and teaches the bearer handler this key.</summary>
    public static void Use(IServiceCollection services, KoleksiOptions settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        /*
         * A fresh key per process. Restarting the API therefore invalidates every token it
         * issued — deliberate even though accounts themselves now persist (see
         * LocalAccountStore): a real, deployed session does not survive the API's own
         * process restarting either, so a browser tab always has to sign in again there too.
         * Only re-registering is the thing LocalAccountStore exists to stop being necessary.
         */
        var signingKey = new LocalSigningKey();
        services.AddSingleton(signingKey);

        services.AddSingleton<LocalAccountStore>();
        services.RemoveAll<IIdentityService>();
        services.AddSingleton<IIdentityService>(provider => new LocalIdentityService(
            signingKey,
            settings.Cognito.Authority,
            settings.Cognito.ClientId,
            provider.GetRequiredService<LocalAccountStore>(),
            provider.GetRequiredService<ILogger<LocalIdentityService>>()));

        /*
         * Both the resolved configuration and the manager are set, mirroring the test
         * host. The handler may take either path depending on how the identity library
         * resolves metadata, and leaving one unset would send it to the real Cognito
         * metadata endpoint for a pool whose id is "REPLACEME".
         */
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = settings.Cognito.Authority };
            configuration.SigningKeys.Add(signingKey.Key);

            options.Configuration = configuration;
            options.ConfigurationManager =
                new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            options.TokenValidationParameters.IssuerSigningKey = signingKey.Key;
        });
    }

    [LoggerMessage(
        Level = Microsoft.Extensions.Logging.LogLevel.Warning,
        Message = "LOCAL IDENTITY IS ACTIVE. Accounts persist to local DynamoDB but tokens are "
            + "signed by this process, not by Cognito, and do not survive a restart. Reached only "
            + "by a Debug build, in the Development environment, with placeholder pool ids; set "
            + "Koleksi:Cognito to a real pool to use the real provider.")]
    public static partial void LogActive(ILogger logger);
}

/// <summary>The RSA key pair this process signs local tokens with.</summary>
internal sealed class LocalSigningKey : IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);

    public LocalSigningKey() => Key = new RsaSecurityKey(rsa) { KeyId = "koleksi-local-dev" };

    public RsaSecurityKey Key { get; }

    public void Dispose() => rsa.Dispose();
}
#endif
