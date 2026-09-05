using System.Net.Http.Headers;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Koleksi.Api.Tests;

/// <summary>
/// Hosts the real <c>Program</c> in-process, with two substitutions and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is replaced.</b> Cognito's published JWKS is swapped for a key this process
/// owns, and the adapters that would reach AWS are swapped for in-memory ones. That is
/// the whole list.
/// </para>
/// <para>
/// <b>What stays real, deliberately.</b> The JWT bearer handler and every one of its
/// validation parameters, the fallback authorization policy, CORS, the security-header
/// middleware, both rate limiters, output caching, routing, model binding, JSON options,
/// and the exception handler. Those are the things this suite exists to prove, and a test
/// host that stubs authentication proves only that the stub works.
/// </para>
/// <para>
/// <b>Why fakes rather than DynamoDB Local.</b> A test that needs a container running is
/// a test that stops being run. The storage layer's own behaviour — key composition,
/// cursor round-tripping, shard stability — is covered by <c>Koleksi.Infrastructure.Tests</c>
/// without a network, and the fakes here keep the two contracts that the API layer
/// actually depends on: reads return a fresh aggregate, and writes enforce the version.
/// </para>
/// </remarks>
public sealed class KoleksiApiFactory : WebApplicationFactory<Program>
{
    public const string Region = "ap-southeast-3";
    public const string UserPoolId = "ap-southeast-3_koleksitest";
    public const string ClientId = "1testclient0koleksi";

    /// <summary>
    /// Matches what <c>CognitoOptions.Authority</c> derives from the region and pool id.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than computed so that a change to the derivation shows up as a
    /// failing authentication test instead of a test that quietly changes with it.
    /// </remarks>
    public const string Issuer = $"https://cognito-idp.{Region}.amazonaws.com/{UserPoolId}";

    /// <summary>The one origin the CORS policy permits.</summary>
    public const string AllowedOrigin = "https://koleksi.test";

    static KoleksiApiFactory() => ApplyConfigurationEnvironment();

    /// <summary>The key the API will accept signatures from.</summary>
    public TestSigningKey SigningKey { get; } = new("koleksi-test-key");

    public FakeListingRepository Listings { get; } = new();

    public FakeAuctionRepository Auctions { get; } = new();

    public FakeListingSearchIndex SearchIndex { get; } = new();

    public FakeMediaStorage Media { get; } = new();

    public FakeIdempotencyStore Idempotency { get; } = new();

    public MutableClock Clock { get; } = new();

    public FakeIdentityService Identity { get; } = new();

    public FakeProfileRepository Profiles { get; } = new();

    public FakePhoneVerificationService PhoneVerification { get; } = new();

    public FakeEmailVerificationService EmailVerification { get; } = new();

    public FakeOfferRepository Offers { get; } = new();

    public FakeOrderRepository Orders { get; } = new();

    public FakeCartRepository Cart { get; } = new();

    public FakePaymentGateway Payments { get; } = new();

    /// <summary>
    /// A client that appears to come from a particular caller address.
    /// </summary>
    /// <remarks>
    /// Anonymous rate limiting partitions on the client address, which behind API Gateway
    /// arrives in <c>X-Forwarded-For</c>. Handing each test its own address is what keeps
    /// one test's budget from being spent by another — and exercises the real
    /// <c>UseForwardedHeaders</c> configuration rather than a substitute for it.
    /// </remarks>
    public HttpClient CreateClientFrom(string clientAddress)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", clientAddress);
        return client;
    }

    /// <summary>A client that presents the given access token on every request.</summary>
    public HttpClient CreateClientWithToken(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>A client carrying a well-formed token for the given subject.</summary>
    public HttpClient CreateClientAs(string subject, string username, params string[] groups) =>
        CreateClientWithToken(TestTokens.Access(SigningKey, new TokenOptions
        {
            Subject = subject,
            Username = username,
            Groups = groups,
        }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, LoopbackConnectionFilter>();

            UseLocallyMintedTokens(services);
            UseInMemoryAdapters(services);
        });
    }

    /// <summary>
    /// Supplies the <c>Koleksi</c> configuration section the way a deployed stage does.
    /// </summary>
    /// <remarks>
    /// Environment variables rather than an in-memory configuration source, for two
    /// reasons. <c>Program</c> reads and validates the section while the builder is still
    /// being constructed, which is earlier than a test-host configuration callback can
    /// reach; and <c>KOLEKSI__SECTION__KEY</c> is exactly how the CDK app supplies these
    /// values in production, so the binding path under test is the real one.
    /// </remarks>
    private static void ApplyConfigurationEnvironment()
    {
        // Not Development: appsettings.Development.json holds placeholder pool ids, and a
        // suite that silently picked them up would be authenticating against nothing.
        Set("ASPNETCORE_ENVIRONMENT", "Testing");

        Set("KOLEKSI__REGION", Region);
        Set("KOLEKSI__COGNITO__USERPOOLID", UserPoolId);
        Set("KOLEKSI__COGNITO__CLIENTID", ClientId);
        Set("KOLEKSI__DYNAMO__TABLENAME", "koleksi-test");
        Set("KOLEKSI__MEDIA__PUBLICBUCKET", "koleksi-test-media");
        Set("KOLEKSI__MEDIA__PRIVATEBUCKET", "koleksi-test-identity");
        Set("KOLEKSI__MEDIA__CDNBASEURL", FakeMediaStorage.CdnBase);
        Set("KOLEKSI__EMAIL__FROMADDRESS", "verifikasi@koleksi.test");
        Set("KOLEKSI__MEDIA__UPLOADGRANTLIFETIME", "00:15:00");
        Set("KOLEKSI__PAYMENT__BASEURL", "https://payments.koleksi.test");
        Set("KOLEKSI__PAYMENT__CLIENTID", "BRN-TEST-0001");
        Set("KOLEKSI__PAYMENT__SECRETARN",
            "arn:aws:secretsmanager:ap-southeast-3:000000000000:secret:koleksi-test-payment");
        Set("KOLEKSI__PAYMENT__RETURNURL", "https://koleksi.test/checkout/selesai");
        Set("KOLEKSI__CORS__ALLOWEDORIGINS__0", AllowedOrigin);

        static void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>
    /// Points issuer-signing-key resolution at a key pair this process holds.
    /// </summary>
    /// <remarks>
    /// Nothing else about the handler is touched. The issuer, the algorithm list, the
    /// thirty-second skew, the <c>client_id</c> check, and the <c>token_use</c> check all
    /// remain as <c>Program</c> configured them, which is the only reason a test that
    /// presents an id token is evidence about production.
    /// </remarks>
    private void UseLocallyMintedTokens(IServiceCollection services) =>
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey.Key);

            // Both are set because the handler may take either path depending on how the
            // identity library resolves configuration; leaving one pointing at the real
            // Cognito metadata endpoint would turn a unit of work into a network call.
            options.Configuration = configuration;
            options.ConfigurationManager =
                new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);

            options.TokenValidationParameters.IssuerSigningKey = SigningKey.Key;
        });

    private void UseInMemoryAdapters(IServiceCollection services)
    {
        Replace<IListingRepository>(services, Listings);
        Replace<IAuctionRepository>(services, Auctions);
        Replace<IListingSearchIndex>(services, SearchIndex);
        Replace<IMediaStorage>(services, Media);
        Replace<IIdempotencyStore>(services, Idempotency);
        Replace<IClock>(services, Clock);

        // Replaced rather than pointed at a test pool: an endpoint test that needs a
        // reachable Cognito is a test that fails when someone's network is slow.
        Replace<IIdentityService>(services, Identity);

        Replace<IProfileRepository>(services, Profiles);
        Replace<IPhoneVerificationService>(services, PhoneVerification);
        Replace<IEmailVerificationService>(services, EmailVerification);

        Replace<IOfferRepository>(services, Offers);
        Replace<IOrderRepository>(services, Orders);
        Replace<ICartRepository>(services, Cart);

        // The one AWS dependency of the bunch that is not in InfrastructureServiceCollectionExtensions'
        // usual shape — see FakePaymentGateway's own remarks on why it exists at all.
        Replace<IPaymentGateway>(services, Payments);

        // ICategoryRepository is deliberately left alone. Its production implementation is
        // a static seed with no AWS dependency, so the browse tests run against the real
        // facet vocabulary — which is what makes "a facet value the caller invented is
        // dropped" a statement about the shipped allowlist rather than about a fixture.
        //
        // The AWS client registrations are also left in place. Nothing resolves them once
        // the adapters above are replaced, and leaving them lets ValidateOnBuild check the
        // production graph rather than a hollowed-out one.
    }

    private static void Replace<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(instance);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SigningKey.Dispose();
        }

        base.Dispose(disposing);
    }
}
