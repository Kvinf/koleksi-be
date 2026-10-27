using System.Net.Http.Headers;
using System.Security.Cryptography;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Koleksi.AdminApi.Tests;

/// <summary>
/// Hosts the real admin <c>Program</c> in-process, with the same two substitutions the public API's suite makes and
/// nothing else: the staff pool's published keys are swapped for a key this process owns, and the adapters that reach
/// AWS are swapped for in-memory ones.
/// </summary>
/// <remarks>
/// The JWT bearer handler and every one of its validation parameters, the authorization policies, the gate, the
/// security headers, the rate limiters, routing and the exception handler all stay real. They are what this suite
/// exists to prove. The host runs as <c>Testing</c>, not <c>Development</c>, so it takes the strict configuration path
/// (a stage name and a shared secret are required) and the local staff identity is never switched on.
/// </remarks>
public sealed class AdminApiFactory : WebApplicationFactory<Program>
{
    public const string Region = "ap-southeast-3";
    public const string StaffPoolId = "ap-southeast-3_staffpooltest";
    public const string StaffClientId = "1staffclient0koleksi";

    /// <summary>Spelled out rather than computed, so a change to how the issuer is derived fails a test instead of following it.</summary>
    public const string StaffIssuer = $"https://cognito-idp.{Region}.amazonaws.com/{StaffPoolId}";

    /// <summary>The public pool, which the admin API must never accept.</summary>
    public const string PublicIssuer = $"https://cognito-idp.{Region}.amazonaws.com/ap-southeast-3_publicpooltest";

    public const string PublicClientId = "1publicclient0koleksi";

    public const string InternalSecret = "test-admin-internal-secret-0123456789abcdef";

    public const string StaffAlice = "11111111-1111-4111-8111-111111111111";

    public const string StaffBob = "22222222-2222-4222-8222-222222222222";

    static AdminApiFactory()
    {
        Set("ASPNETCORE_ENVIRONMENT", "Testing");
        Set("KOLEKSIADMIN__REGION", Region);
        Set("KOLEKSIADMIN__STAGE", "test");
        Set("KOLEKSIADMIN__STAFFPOOL__USERPOOLID", StaffPoolId);
        Set("KOLEKSIADMIN__STAFFPOOL__CLIENTID", StaffClientId);
        Set("KOLEKSIADMIN__DYNAMO__TABLENAME", "koleksi-test");
        Set("KOLEKSIADMIN__SECURITY__INTERNALSECRET", InternalSecret);

        static void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value);
    }

    public SigningKey Key { get; } = new("koleksi-admin-test-key");

    public MutableClock Clock { get; } = new();

    public FakeAuditLog Audit { get; } = new();

    public FakeIdempotencyStore Idempotency { get; } = new();

    public FakeOrderRepository Orders { get; } = new();

    public FakeDisputeStore Disputes { get; }

    public FakeHolidayStore Holidays { get; }

    public FakePublicProfiles Profiles { get; } = new();

    public FakeConversations Conversations { get; } = new();

    public FakeRefundSender Refunds { get; } = new();

    public AdminApiFactory()
    {
        Disputes = new FakeDisputeStore(Orders, Audit);
        Holidays = new FakeHolidayStore(Audit);
        foreach (var (year, seed) in HolidaySeed.ByYear)
        {
            Holidays.Preload(year, seed);
        }
    }

    /// <summary>A client carrying the gate secret but no token: what the web tier is before a staff member signs in.</summary>
    public HttpClient CreateWebTierClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Koleksi-Admin-Internal", InternalSecret);
        return client;
    }

    /// <summary>The web tier presenting the given access token.</summary>
    public HttpClient CreateWebTierClientWithToken(string accessToken)
    {
        var client = CreateWebTierClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>The web tier presenting a well-formed staff token for the given staff member.</summary>
    public HttpClient CreateStaffClient(string staffId = StaffAlice, string name = "alice") =>
        CreateWebTierClientWithToken(Tokens.Access(Key, new TokenSpec { Subject = staffId, Username = name }));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = StaffIssuer };
                configuration.SigningKeys.Add(Key.Key);
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters.IssuerSigningKey = Key.Key;
            });

            Replace<IAuditLog>(services, Audit);
            Replace<IIdempotencyStore>(services, Idempotency);
            Replace<IClock>(services, Clock);

            // Milestone 1. Replaced for every test, not only the ones that read them: the holiday seeder runs at startup and must never
            // reach for a real table from a test host.
            Replace<IOrderRepository>(services, Orders);
            Replace<IDisputeAdminStore>(services, Disputes);
            Replace<IHolidayStore>(services, Holidays);
            Replace<IPublicProfileRepository>(services, Profiles);
            Replace<IConversationRepository>(services, Conversations);
            Replace<IRefundSender>(services, Refunds);
        });
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
            Key.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>An RSA key pair this process owns, standing in for the staff pool's published keys.</summary>
public sealed class SigningKey : IDisposable
{
    private readonly RSA rsa = RSA.Create(2048);

    public SigningKey(string keyId) => Key = new RsaSecurityKey(rsa) { KeyId = keyId };

    public RsaSecurityKey Key { get; }

    public byte[] PublicKeyBytes => rsa.ExportSubjectPublicKeyInfo();

    public void Dispose() => rsa.Dispose();
}

/// <summary>How one minted token should differ from a well-formed staff token. Every field defaults to the valid value.</summary>
public sealed record TokenSpec
{
    public string Subject { get; init; } = AdminApiFactory.StaffAlice;

    public string Username { get; init; } = "alice";

    public string Issuer { get; init; } = AdminApiFactory.StaffIssuer;

    public string ClientId { get; init; } = AdminApiFactory.StaffClientId;

    public string TokenUse { get; init; } = "access";

    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);

    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}

public static class Tokens
{
    private static readonly JsonWebTokenHandler Handler = new();

    public static string Access(SigningKey key, TokenSpec? spec = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Handler.CreateToken(Describe(spec ?? new TokenSpec(), new SigningCredentials(key.Key, SecurityAlgorithms.RsaSha256)));
    }

    /// <summary>The algorithm-confusion attack: HS256 with the public key as the shared secret.</summary>
    public static string HmacSignedWithThePublicKey(SigningKey key, TokenSpec? spec = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var secret = new SymmetricSecurityKey(key.PublicKeyBytes) { KeyId = key.Key.KeyId };
        return Handler.CreateToken(Describe(spec ?? new TokenSpec(), new SigningCredentials(secret, SecurityAlgorithms.HmacSha256)));
    }

    private static SecurityTokenDescriptor Describe(TokenSpec spec, SigningCredentials credentials) =>
        new()
        {
            Issuer = spec.Issuer,
            IssuedAt = spec.IssuedAt.UtcDateTime,
            NotBefore = spec.IssuedAt.UtcDateTime,
            Expires = (spec.IssuedAt + spec.Lifetime).UtcDateTime,
            SigningCredentials = credentials,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sub"] = spec.Subject,
                ["username"] = spec.Username,
                ["client_id"] = spec.ClientId,
                ["token_use"] = spec.TokenUse,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };
}

public sealed class MutableClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 19, 3, 0, 0, TimeSpan.Zero);
}

/// <summary>An append-only log in memory, optionally failing, so a test can see what a failed audit write does to the action.</summary>
public sealed class FakeAuditLog : IAuditLog
{
    private readonly List<AuditEntry> entries = [];

    public bool FailNextAppend { get; set; }

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (entries)
            {
                return [.. entries];
            }
        }
    }

    public Task AppendAsync(AuditEntry entry, CancellationToken ct)
    {
        if (FailNextAppend)
        {
            FailNextAppend = false;
            throw new InvalidOperationException("The audit store is unavailable.");
        }

        lock (entries)
        {
            entries.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<AuditPage> ListAsync(AuditQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        IEnumerable<AuditEntry> selected = Entries.OrderByDescending(e => e.At);
        if (query.ActorId is not null)
        {
            selected = selected.Where(e => e.ActorId == query.ActorId);
        }

        if (query.TargetId is not null)
        {
            selected = selected.Where(e => e.TargetType == query.TargetType && e.TargetId == query.TargetId);
        }

        return Task.FromResult(new AuditPage([.. selected.Take(query.Limit)], null));
    }
}

public sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<string, string?> claims = new(StringComparer.Ordinal);

    public Task<bool> TryClaimAsync(string scope, string key, TimeSpan retention, CancellationToken ct)
    {
        lock (claims)
        {
            return Task.FromResult(claims.TryAdd(scope + "#" + key, null));
        }
    }

    public Task RecordResultAsync(string scope, string key, string resultJson, CancellationToken ct)
    {
        lock (claims)
        {
            claims[scope + "#" + key] = resultJson;
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetRecordedResultAsync(string scope, string key, CancellationToken ct)
    {
        lock (claims)
        {
            return Task.FromResult(claims.GetValueOrDefault(scope + "#" + key));
        }
    }

    public Task ReleaseClaimAsync(string scope, string key, CancellationToken ct)
    {
        lock (claims)
        {
            if (claims.TryGetValue(scope + "#" + key, out var result) && result is null)
            {
                claims.Remove(scope + "#" + key);
            }
        }

        return Task.CompletedTask;
    }
}
