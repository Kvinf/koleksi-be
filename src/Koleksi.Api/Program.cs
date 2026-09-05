using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Koleksi.Api;
#if DEBUG
using Koleksi.Api.Development;
#endif
using Koleksi.Api.Endpoints;
using Koleksi.Api.Middleware;
using Koleksi.Api.Realtime;
using Koleksi.Api.Security;
using Koleksi.Application;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ─── Hosting ────────────────────────────────────────────────────────────────────
// Translates API Gateway's HTTP API (payload v2) events into ASP.NET Core requests.
// The same Program runs unchanged with "dotnet run" locally, so there is no
// Lambda-shaped code path that only executes in production and is never exercised.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// ─── Configuration ──────────────────────────────────────────────────────────────
var settings = builder.Configuration.GetSection(KoleksiOptions.SectionName).Get<KoleksiOptions>()
    ?? throw new InvalidOperationException(
        $"Configuration section '{KoleksiOptions.SectionName}' is missing. See docs/configuration.md.");
settings.Validate(allowLocalEndpoints: builder.Environment.IsDevelopment());

// ─── Logging ────────────────────────────────────────────────────────────────────
// JSON to stdout, which CloudWatch ingests as structured fields. No third-party
// logging stack: fewer dependencies in the request path is both faster and less to
// audit, and structured console logging is a first-class feature of the platform.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

// ─── JSON ───────────────────────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

    // Enums cross the wire as names. Ordinals would silently change meaning the first
    // time someone inserts a member into the middle of an enum.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// ─── Authentication ─────────────────────────────────────────────────────────────
// Tokens are minted by Cognito and verified here against the pool's published JWKS.
// The API holds no password material and no signing key, so a compromise of this
// service cannot mint a token for anyone.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = settings.Cognito.Authority;
        options.MetadataAddress = $"{settings.Cognito.Authority}/.well-known/openid-configuration";

        // Reject tokens over plaintext transport outside development.
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

        // Claims keep the names the token gave them.
        //
        // The default is to rewrite well-known claims into the WS-Federation URIs that
        // .NET inherited from WIF, which renames "sub" to a schemas.xmlsoap.org URI while
        // leaving Cognito's own claims untouched. The result is a principal that disagrees
        // with its token: ICurrentUser.UserId reads "sub" and finds nothing, so every
        // authenticated caller is treated as anonymous and the rate limiter partitions
        // them all by IP. Turning the mapping off is what makes the claim names in this
        // file mean what they say.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Cognito.Authority,

            // Cognito access tokens carry the app client id in "client_id" rather than
            // in "aud", so the audience is validated explicitly below instead.
            ValidateAudience = false,

            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,

            // Default is five minutes, which meaningfully extends the life of a stolen
            // token. Thirty seconds absorbs ordinary clock drift and nothing more.
            ClockSkew = TimeSpan.FromSeconds(30),

            // Only asymmetric RS256, as Cognito issues. Leaving the algorithm open
            // invites the "alg: none" and HMAC-with-public-key confusion attacks.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var clientId = context.Principal?.FindFirst("client_id")?.Value;
                if (!string.Equals(clientId, settings.Cognito.ClientId, StringComparison.Ordinal))
                {
                    context.Fail("Token was issued to a different application client.");
                    return Task.CompletedTask;
                }

                // "access" tokens authorise API calls; "id" tokens describe a user to a
                // client and must never be accepted as a credential here.
                var tokenUse = context.Principal?.FindFirst("token_use")?.Value;
                if (!string.Equals(tokenUse, "access", StringComparison.Ordinal))
                {
                    context.Fail("Only access tokens are accepted.");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    // Any endpoint without an explicit policy still requires a valid token. Opting out
    // is then a visible, reviewable "AllowAnonymous" rather than an omission.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy(AuthorizationPolicies.Staff, policy =>
        policy.RequireAuthenticatedUser().RequireClaim("cognito:groups", "staff"));
});

// ─── CORS ───────────────────────────────────────────────────────────────────────
// An explicit origin allowlist from configuration. Reflecting the request's Origin, or
// pairing a wildcard with credentials, would let any site on the internet make
// authenticated calls on a signed-in user's behalf.
builder.Services.AddCors(options => options.AddPolicy(CorsPolicies.Frontend, policy => policy
    .WithOrigins(settings.Cors.AllowedOrigins)
    .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "Accept-Language")
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    .AllowCredentials()
    .SetPreflightMaxAge(TimeSpan.FromHours(1))));

// ─── Rate limiting ──────────────────────────────────────────────────────────────
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

    // Partition by user when we know who is calling, and by client IP otherwise. Keying
    // solely on IP would let one user behind a shared mobile NAT exhaust the budget for
    // a whole city; keying solely on identity would leave anonymous traffic unlimited.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var partitionKey = context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 120,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });

    // Sign-in and sign-up get the tightest budget in the product. There is no identity to
    // partition by yet, so this is keyed on the caller's address — and it is deliberately
    // small, because repeated guessing is the entire attack on this endpoint. Ten attempts
    // to start, then five a minute: ample for a person who mistyped their password, and
    // useless for working through a credential dump.
    options.AddPolicy(RateLimitPolicies.Authentication, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 5,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Bidding gets its own tighter budget. It is the most abusable write in the product
    // and the most expensive to serve under contention.
    options.AddPolicy(RateLimitPolicies.Bidding, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 15,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
});

// ─── Application services ───────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(settings);

// In-process only — see AuctionEventHub's own remarks on what a multi-instance
// deployment would need instead.
builder.Services.AddSingleton<AuctionEventHub>();
builder.Services.AddSingleton<IAuctionEventPublisher>(provider => provider.GetRequiredService<AuctionEventHub>());

#if DEBUG
// ─── Local identity ─────────────────────────────────────────────────────────────
// Development convenience, compiled out of every Release build. See
// Development/LocalIdentity.cs for the three conditions that gate it and why each is
// there. A Debug build with a real pool configured takes the Cognito path unchanged.
var localIdentity =
    builder.Environment.IsDevelopment() && LocalIdentity.IsUnconfigured(settings.Cognito);

if (localIdentity)
{
    LocalIdentity.Use(builder.Services, settings);
}

// Declared here, outside the environment check below, so it is still in scope — and
// correctly false — when the endpoint mapping near the bottom of this file asks whether
// to map LocalMediaEndpoints in a non-Development environment.
var localMedia = false;

// ─── Local phone verification ────────────────────────────────────────────────────
// Independent of the Cognito placeholder check above: SNS needs no pool id to be
// unconfigured for, and a developer with a real pool configured still has no SMS gateway
// on their desk. Gated on environment alone, plus the same #if DEBUG compile-time lock.
if (builder.Environment.IsDevelopment())
{
    builder.Services.RemoveAll<IPhoneVerificationService>();
    builder.Services.AddSingleton<IPhoneVerificationService, LocalPhoneVerificationService>();

    // Same reasoning, same gate: no SES identity to send from locally either.
    builder.Services.RemoveAll<IEmailVerificationService>();
    builder.Services.AddSingleton<IEmailVerificationService, LocalEmailVerificationService>();

    // ─── Local media storage ───────────────────────────────────────────────────
    // Unlike phone/email above, this one has a real alternative: with
    // Koleksi:Media:ServiceUrl set (docker-compose's MinIO, by default) the ordinary
    // S3MediaStorage registered in AddInfrastructure is already wired against it — real
    // presigned PUTs, real signature verification, just against MinIO instead of AWS — so
    // it is left in place unchanged. Only a developer who has not started MinIO falls back
    // to this in-memory stand-in.
    localMedia = string.IsNullOrWhiteSpace(settings.Media.ServiceUrl);
    if (localMedia)
    {
        builder.Services.RemoveAll<IMediaStorage>();
        builder.Services.AddSingleton<LocalMediaStorage>();
        builder.Services.AddSingleton<IMediaStorage>(provider => provider.GetRequiredService<LocalMediaStorage>());
    }
}
#endif

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddOutputCache();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOpenApi();
}

// Resolve the whole graph at startup rather than on first use. A handler added without its
// repository registered then fails the deployment instead of a 500 in the middle of a
// checkout. Scope validation catches the mirror-image mistake: a singleton capturing
// ICurrentUser would serve one caller's identity to everyone on a warm Lambda.
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

var app = builder.Build();

#if DEBUG
if (localIdentity)
{
    // Loud, once, at startup. A developer who forgets which provider is answering will
    // otherwise conclude that Cognito accepted a password it has never seen.
    LocalIdentity.LogActive(app.Logger);
}
#endif

// ─── Pipeline ───────────────────────────────────────────────────────────────────
// Order is load-bearing. Security headers first so they apply even to responses
// produced by the exception handler; authentication before authorisation; rate
// limiting after authentication so limits can be partitioned by identity.

// API Gateway terminates TLS and forwards the caller's address in X-Forwarded-For.
// Without this the rate limiter would partition every request by the gateway's own
// address, i.e. one bucket for the entire internet.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardedForHeaderName = "X-Forwarded-For",
});

app.UseExceptionHandler();
app.UseSecurityHeaders();
app.UseCors(CorsPolicies.Frontend);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseOutputCache();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness only — deliberately not touching DynamoDB or Cognito. A health check that
// probes dependencies turns a downstream blip into a load balancer pulling every
// healthy instance out of service.
app.MapHealthChecks("/health").AllowAnonymous();

app.MapAuthEndpoints();
app.MapCatalogEndpoints();
app.MapAuctionEndpoints();
app.MapProfileEndpoints();
app.MapMediaEndpoints();
app.MapListingEndpoints();
app.MapOfferEndpoints();
app.MapOrderEndpoints();
app.MapCartEndpoints();
app.MapPaymentEndpoints();

#if DEBUG
if (localMedia)
{
    // Only mapped when LocalMediaStorage replaced the S3 adapter above — these routes
    // are what its presigned-looking upload URLs actually point at. With MinIO
    // configured, uploads go straight to it and this door does not exist.
    app.MapLocalMediaEndpoints();
}
#endif

await app.RunAsync().ConfigureAwait(false);

/// <summary>Exposed so the integration test host can reference the entry point assembly.</summary>
public partial class Program;
