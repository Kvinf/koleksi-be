using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Koleksi.Api;
using Koleksi.Api.Development;
using Koleksi.Api.Endpoints;
using Koleksi.Api.Middleware;
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
        // Public catalogue reads (browse, item page, categories, a seller's public shop) are what every page view of the site
        // spends: the web tier makes 2 to 4 API calls per page. On the general budget below, about 15
        // page views a minute from one address exhausted it and every page turned into an error, and
        // a mobile carrier's shared address makes that ordinary rather than abusive. They get a
        // separate, larger bucket per address, apart from everything else so spending it never
        // starves a sign-in or a write. The data is public, identical for everyone and cheap to
        // serve; the per-route limits on sign-in, bidding, checkout and the rest are unchanged.
        if (HttpMethods.IsGet(context.Request.Method)
            && (context.Request.Path.StartsWithSegments("/api/catalog", StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.StartsWithSegments("/api/sellers", StringComparison.OrdinalIgnoreCase)))
        {
            return RateLimitPartition.GetTokenBucketLimiter(
                "catalog:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 600,
                    TokensPerPeriod = 300,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        }

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

    // Starting a checkout: a burst of 10 covers a buyer trying a few items in a row, then 10 per ten
    // minutes. Per user, since the endpoints are never anonymous.
    options.AddPolicy(RateLimitPolicies.Checkout, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Asking for a verification code (SMS or email): 3 to start, then 3 per 10 minutes. Per user.
    options.AddPolicy(RateLimitPolicies.VerificationCode, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 3,
                TokensPerPeriod = 3,
                ReplenishmentPeriod = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Changing your own password: the caller is signed in, so the thing to bound is guesses at
    // the current password by someone holding a stolen access token. Per user; five to start,
    // then two a minute — a person who mistyped needs three tries, a guesser needs thousands.
    options.AddPolicy(RateLimitPolicies.PasswordChange, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 5,
                TokensPerPeriod = 2,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Sending chat messages: a burst of ten covers a quick back-and-forth, then ten a minute.
    // Keyed on the user, since the endpoint is never anonymous.
    options.AddPolicy(RateLimitPolicies.Messaging, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Hearts: a burst of 30 covers ticking through a results page, then 30 a minute.
    options.AddPolicy(RateLimitPolicies.Watchlist, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 30,
                TokensPerPeriod = 30,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Writing a review: a burst of five covers a buyer catching up on several delivered orders,
    // then five per ten minutes. Keyed on the user, since the endpoint is never anonymous.
    options.AddPolicy(RateLimitPolicies.Reviews, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 5,
                TokensPerPeriod = 5,
                ReplenishmentPeriod = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Connect tickets: a burst of 20 covers several tabs reconnecting together, then 20 a minute.
    options.AddPolicy(RateLimitPolicies.Realtime, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.User.FindFirst("sub")?.Value ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                TokensPerPeriod = 20,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Search suggestions are anonymous, so they are keyed on the caller's address: a burst of 60
    // covers a person typing a few searches in a row, then 60 a minute.
    options.AddPolicy(RateLimitPolicies.Suggest, context =>
        RateLimitPartition.GetTokenBucketLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 60,
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

// ─── Simulators ──────────────────────────────────────────────────────────────────
// Until the payment gateway, a courier provider and the payout provider are connected, an order is walked through
// payment, shipping and payout by hand from the simulator page. Everything else is the real code. Off unless
// Koleksi:Simulators:Enabled is set, and KoleksiOptions.Validate has already refused it outside a developer's
// machine or the dev stage, so this never runs where real money moves.
var simulators = settings.Simulators.Enabled;
if (simulators)
{
    builder.Services.RemoveAll<IDeliveryTracker>();
    builder.Services.AddSingleton<IDeliveryTracker, LocalDeliveryTracker>();
    builder.Services.RemoveAll<IEscrowPayout>();
    builder.Services.AddSingleton<IEscrowPayout, LocalEscrowPayout>();
}

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

// ─── Local live updates ──────────────────────────────────────────────────────────
// With no WebSocket API to push through (Koleksi:Realtime:ManagementUrl unset), a developer's
// run holds the sockets itself, in this process — the same protocol, so the frontend's client
// is exercised unchanged. A deployed stage, and a Release build, never take this branch.
var localRealtime = builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(settings.Realtime.ManagementUrl);
if (localRealtime)
{
    builder.Services.RemoveAll<IRealtimeBroadcaster>();
    builder.Services.AddSingleton<LocalRealtimeHub>();
    builder.Services.AddSingleton<IRealtimeBroadcaster>(provider => provider.GetRequiredService<LocalRealtimeHub>());
}

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
    //
    // KOLEKSI_FORCE_REAL_MEDIA=true skips that fallback even with ServiceUrl blank, for a
    // developer who wants real S3 (the actual CDK-provisioned buckets) while still getting
    // the Development-only conveniences above (fake identity, no SMS/SES). BuildS3Client
    // resolves real AWS credentials in exactly that case — an empty ServiceUrl is what
    // selects the real credential chain there, the same as it does for Koleksi:Dynamo.
    localMedia = string.IsNullOrWhiteSpace(settings.Media.ServiceUrl)
        && Environment.GetEnvironmentVariable("KOLEKSI_FORCE_REAL_MEDIA") != "true";
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

// The web tier reaches this API through the same gateway, so every visitor would otherwise
// share its egress address (and the address-keyed rate-limit buckets). When a shared secret is
// configured, a request that proves it came from the web tier may name the visitor's address.
// After UseForwardedHeaders so it has the final say, before the limiter so the limiter sees it.
// Off entirely when no secret is set: the header is then never read.
if (settings.Security.HasInternalSecret)
{
    app.UseMiddleware<TrustedClientIpMiddleware>(settings.Security.InternalSecret!);
}

app.UseExceptionHandler();
app.UseSecurityHeaders();

// Before anything reads a body: a request larger than any this API accepts is refused here (413).
app.UseMiddleware<RequestBodyLimitMiddleware>();
app.UseCors(CorsPolicies.Frontend);

#if DEBUG
if (localRealtime)
{
    app.UseWebSockets();
}
#endif

app.UseAuthentication();
app.UseAuthorization();

// A banned account cannot start anything new (it can still read, sign out and finish paid orders).
app.UseMiddleware<BannedAccountMiddleware>();
app.UseRateLimiter();

// An id that cannot be one of ours is a 404 here, not a DynamoDB key error further in.
app.UseMiddleware<WellFormedIdMiddleware>();
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
app.MapPasswordEndpoints();
app.MapCatalogEndpoints();
app.MapAuctionEndpoints();
app.MapProfileEndpoints();
app.MapMediaEndpoints();
app.MapListingEndpoints();
app.MapOfferEndpoints();
app.MapConversationEndpoints();
app.MapRealtimeEndpoints();
app.MapOrderEndpoints();
app.MapCartEndpoints();
app.MapPayoutEndpoints();
app.MapCaseEndpoints();
app.MapSellerEndpoints();
app.MapReviewEndpoints();
app.MapWatchlistEndpoints();
app.MapPaymentEndpoints();
app.MapShippingWebhookEndpoints();
app.MapInternalEndpoints();

// A laptop has no payment gateway to send a signed webhook, so development gets a route that marks an order
// paid. Compiled out of every Release build, so it does not exist in a deployed stage whatever its environment says.
#if DEBUG
if (app.Environment.IsDevelopment())
{
    app.MapDevelopmentPaymentEndpoint();
}
#endif

if (simulators)
{
    app.MapSimulatorEndpoints();
}

#if DEBUG
if (localRealtime)
{
    // The WebSocket door the browser connects to when this process is the hub. Anonymous on
    // purpose, like the deployed API Gateway route: a connection alone grants nothing, and
    // a user's private channel is joined only by redeeming a ticket.
    app.Map("/realtime", (HttpContext context, LocalRealtimeHub hub) => hub.HandleAsync(context))
        .AllowAnonymous();
}

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
