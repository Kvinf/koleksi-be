using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Koleksi.AdminApi;
using Koleksi.AdminApi.Endpoints;
using Koleksi.AdminApi.Middleware;
using Koleksi.AdminApi.Security;
using Koleksi.Application;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

#if DEBUG
using Koleksi.AdminApi.Development;
#endif

var builder = WebApplication.CreateBuilder(args);

// ─── Hosting ────────────────────────────────────────────────────────────────────
// Same hosting model as the public API: API Gateway HTTP API (payload v2) events become ASP.NET Core
// requests, and the same Program runs unchanged under "dotnet run".
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// ─── Configuration ──────────────────────────────────────────────────────────────
// A different section from the public API's, so neither can be started with the other's settings.
var settings = builder.Configuration.GetSection(KoleksiAdminOptions.SectionName).Get<KoleksiAdminOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{KoleksiAdminOptions.SectionName}' is missing. See docs/configuration.md.");
settings.Validate(allowLocalEndpoints: builder.Environment.IsDevelopment());

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// ─── Authentication: the staff pool, and only the staff pool ────────────────────
// Tokens are minted by the staff user pool and verified against its published JWKS. A token from the public
// pool has a different issuer and is refused here, whatever else is true of it.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = settings.StaffPool.Authority;
        options.MetadataAddress = $"{settings.StaffPool.Authority}/.well-known/openid-configuration";
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // Claims keep the names the token gave them (see the public API's Program.cs for the trap this avoids).
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.StaffPool.Authority,
            ValidateAudience = false,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // Cognito access tokens name the app client in client_id, not aud. A token minted for any other
                // client of this pool is refused.
                var clientId = context.Principal?.FindFirst("client_id")?.Value;
                if (!string.Equals(clientId, settings.StaffPool.ClientId, StringComparison.Ordinal))
                {
                    context.Fail("Token was issued to a different application client.");
                    return Task.CompletedTask;
                }

                // Access tokens authorise API calls; an id token must never be accepted as a credential.
                if (!string.Equals(context.Principal?.FindFirst("token_use")?.Value, "access", StringComparison.Ordinal))
                {
                    context.Fail("Only access tokens are accepted.");
                }

                return Task.CompletedTask;
            },
        };
    });

// ─── Authorization: one policy per capability ───────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    // Anything without an explicit policy still needs a signed-in staff member.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

    foreach (var capability in AdminCapabilities.All)
    {
        options.AddPolicy(capability, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => StaffCapabilities.IsGranted(context.User, capability)));
    }
});

// ─── Rate limiting ──────────────────────────────────────────────────────────────
// Per staff member. Every route needs a token, so this is not about strangers (the gate and the allowlist handle
// those); it bounds a runaway script or a stolen token. The staff UI is a handful of people clicking.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            new { code = "rate_limited", detail = "Too many requests. Please slow down." },
            cancellationToken: token).ConfigureAwait(false);
    };

    static string Partition(HttpContext context) =>
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetTokenBucketLimiter(Partition(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    options.AddPolicy(AdminRateLimits.Write, context =>
        RateLimitPartition.GetTokenBucketLimiter("write:" + Partition(context), _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 20,
            TokensPerPeriod = 20,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

// ─── Services ───────────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentStaff, CurrentStaff>();
builder.Services.AddAdminApplication();
builder.Services.AddAdminInfrastructure(settings);
builder.Services.AddHostedService<HolidaySeedService>();

#if DEBUG
// ─── Local staff identity ───────────────────────────────────────────────────────
// Development convenience, compiled out of every Release build. See Development/LocalStaffIdentity.cs for the four
// locks and what each is for. A Debug build with a real staff pool configured takes the Cognito path unchanged.
var localStaff = builder.Environment.IsDevelopment() && LocalStaffIdentity.IsUnconfigured(settings);
if (localStaff)
{
    LocalStaffIdentity.Use(builder.Services, settings, builder.Configuration);
}
#endif

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AdminExceptionHandler>();
builder.Services.AddHealthChecks();

// Resolve the whole graph at startup, so a handler added without its repository fails the deployment rather than a
// request, and a singleton that captured the per-request staff identity fails at once.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

var app = builder.Build();

#if DEBUG
if (localStaff)
{
    LocalStaffIdentity.LogActive(app.Logger);
}
#endif

// ─── Pipeline ───────────────────────────────────────────────────────────────────
// Order is load-bearing. Forwarded headers first (rate limiting needs the real address); security headers before
// anything that can fail, so even a refusal carries them; the gate before authentication, so a request that is not
// from the web tier is turned away before a token is even parsed. There is no CORS middleware at all: the browser
// never calls this API, only the admin web tier does, so no origin is ever allowed.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardedForHeaderName = "X-Forwarded-For",
});

app.UseExceptionHandler();
app.UseMiddleware<AdminSecurityHeadersMiddleware>();

// An empty secret turns the gate off, which Validate allows only on a developer's machine. It is passed as an empty string and
// not null: UseMiddleware reads a lone null argument as "no arguments at all" and then cannot find the constructor.
app.UseMiddleware<AdminGateMiddleware>(settings.Security.InternalSecret ?? string.Empty);

// After the gate, so an outsider still sees only 404 and never learns this API exists from a 413. Before authentication and before
// anything reads a body: the largest thing staff ever send is a reason of a thousand characters.
app.UseMiddleware<AdminBodyLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness only, and it says nothing about the system. The one anonymous route.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapAdminEndpoints();
app.MapBackofficeEndpoints();

#if DEBUG
if (localStaff)
{
    LocalStaffIdentity.MapEndpoints(app);
}
#endif

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program;
