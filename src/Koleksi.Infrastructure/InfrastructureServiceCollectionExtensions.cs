using Amazon;
using Amazon.ApiGatewayManagementApi;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SimpleEmailV2;
using Amazon.SimpleNotificationService;
using Koleksi.Application.Abstractions;
using Koleksi.Application.Features.Standing;
using Koleksi.Infrastructure.Identity;
using Koleksi.Infrastructure.Media;
using Koleksi.Infrastructure.Notifications;
using Koleksi.Infrastructure.Delivery;
using Koleksi.Infrastructure.Payments;
using Koleksi.Infrastructure.Persistence;
using Koleksi.Infrastructure.Realtime;
using Microsoft.Extensions.DependencyInjection;

namespace Koleksi.Infrastructure;

/// <summary>Registers the adapters that talk to the outside world.</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Wires up DynamoDB, S3, Secrets Manager, and the repositories built on them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration is explicit. Assembly scanning would register whatever happened
    /// to be in this assembly — including a half-finished type someone added while
    /// exploring — and the resulting graph is invisible in review.
    /// </para>
    /// <para>
    /// <b>On lifetimes.</b> AWS SDK clients are thread-safe and expensive to construct, so
    /// they are singletons; a per-request client would open a fresh TLS connection on every
    /// call. Repositories are stateless wrappers over those clients and are singletons too.
    /// Anything that reads per-request identity must be scoped — see
    /// <c>AddApplication</c>, where the handlers live.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, KoleksiOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        // The nested option objects are registered individually so an adapter declares the
        // one section it needs. Injecting the whole KoleksiOptions everywhere would give
        // the media adapter a reference to the payment secret's ARN for no reason.
        services.AddSingleton(options);
        services.AddSingleton(options.Cognito);
        services.AddSingleton(options.Dynamo);
        services.AddSingleton(options.Media);
        services.AddSingleton(options.Email);
        services.AddSingleton(options.Payment);
        services.AddSingleton(options.Delivery);
        services.AddSingleton(options.Realtime);
        services.AddSingleton(options.Security);
        services.AddSingleton(options.Features);
        services.AddSingleton<IAmazonDynamoDB>(_ => BuildDynamoClient(options.Dynamo, options.Region));
        services.AddSingleton<IAmazonS3>(_ => BuildS3Client(options));
        services.AddSingleton<IAmazonSecretsManager>(_ => BuildSecretsClient(options));
        services.AddSingleton<IAmazonSimpleNotificationService>(_ => BuildSnsClient(options));
        services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => BuildSesClient(options));

        services.AddSingleton<IListingRepository, DynamoListingRepository>();
        services.AddSingleton<IAuctionRepository, DynamoAuctionRepository>();
        services.AddSingleton<IOfferRepository, DynamoOfferRepository>();
        services.AddSingleton<IConversationRepository, DynamoConversationRepository>();
        services.AddSingleton<IOrderRepository, DynamoOrderRepository>();
        services.AddSingleton<IOrderSweepBackfill, DynamoOrderRepository>();
        services.AddSingleton<IListingHoldStore, DynamoListingHoldStore>();
        services.AddSingleton<IAccountStandingRepository, DynamoAccountStandingRepository>();
        services.AddSingleton<IPayoutRepository, DynamoPayoutRepository>();
        services.AddSingleton<ICaseStore, DynamoCaseStore>();
        services.AddSingleton<IPaymentOrphanStore, DynamoPaymentOrphanStore>();
        services.AddSingleton<IShippingClaimStore, DynamoShippingClaimStore>();
        services.AddSingleton<ICartRepository, DynamoCartRepository>();
        services.AddSingleton<IWatchlistRepository, DynamoWatchlistRepository>();
        services.AddSingleton<DynamoListingSearchIndex>();
        services.AddSingleton<IListingSearchIndex>(sp => sp.GetRequiredService<DynamoListingSearchIndex>());
        services.AddSingleton<IListingSuggestionIndex>(sp => sp.GetRequiredService<DynamoListingSearchIndex>());
        services.AddSingleton<ICategoryRepository, SeededCategoryRepository>();
        services.AddSingleton<IIdempotencyStore, DynamoIdempotencyStore>();
        services.AddSingleton<IProfileRepository, DynamoProfileRepository>();
        services.AddSingleton<IPublicProfileRepository, DynamoPublicProfileRepository>();
        services.AddSingleton<IReviewRepository, DynamoReviewRepository>();
        services.AddSingleton<DynamoOtpChallengeStore>();
        services.AddSingleton<IVerificationThrottle, DynamoVerificationThrottle>();
        services.AddSingleton<IPhoneVerificationService, SnsPhoneVerificationService>();
        services.AddSingleton<IEmailVerificationService, SesEmailVerificationService>();

        services.AddSingleton<IRealtimeTicketStore, DynamoRealtimeTicketStore>();
        AddRealtimeBroadcaster(services, options);

        services.AddSingleton<IMediaStorage, S3MediaStorage>();

        // Anonymous credentials on purpose: sign-in and sign-up are unauthenticated
        // Cognito APIs, so the identity path is given no AWS authority whatsoever. A
        // compromise of this service inherits nothing it could do to the user pool.
        services.AddSingleton<IAmazonCognitoIdentityProvider>(_ => new AmazonCognitoIdentityProviderClient(
            new AnonymousAWSCredentials(),
            Bounded(new AmazonCognitoIdentityProviderConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.Cognito.ResolvedRegion),
            })));

        // Google sign-in's authorization-code exchange is a plain OAuth token-endpoint POST,
        // not a Cognito SDK call, so it needs an ordinary HttpClient. Factory-managed even
        // though CognitoIdentityService is a singleton — see the constant's doc comment on
        // CognitoIdentityService for why a field-held HttpClient would be the wrong choice.
        services.AddHttpClient(CognitoIdentityService.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddSingleton<IIdentityService, CognitoIdentityService>();

        services.AddSingleton<PaymentSecret>();
        services.AddSingleton<IPaymentGateway, PaymentGateway>();

        // Paying sellers out and learning that a parcel arrived are calls to outside providers. Payout shares
        // the payment gateway's merchant configuration; courier tracking is a separate provider that has not
        // been chosen. Both answer honestly "not configured" until their adapters exist (see each class), and
        // the development host swaps in local simulators (Program.cs).
        services.AddSingleton<IEscrowPayout, GatewayEscrowPayout>();
        AddDeliveryProvider(services, options.Delivery);

        return services;
    }

    /// <summary>
    /// Wires the admin API: DynamoDB, the idempotency store and the audit log, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="AddInfrastructure"/>: that registers the whole marketplace (S3, SES, SNS, the payment
    /// gateway, the public Cognito client), none of which the admin Lambda has permission to use or should be able to
    /// reach. Each milestone adds the repositories its screens need here, so the Lambda's reach grows only as fast as
    /// the code that justifies it.
    /// </remarks>
    public static IServiceCollection AddAdminInfrastructure(this IServiceCollection services, KoleksiAdminOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(options.StaffPool);
        services.AddSingleton(options.Dynamo);
        services.AddSingleton<IAmazonDynamoDB>(_ => BuildDynamoClient(options.Dynamo, options.Region));

        services.AddSingleton<IIdempotencyStore, DynamoIdempotencyStore>();
        services.AddSingleton<IAuditLog, DynamoAuditLog>();

        // Milestone 1: disputes. Orders, public profiles (for handles) and conversations are read through the same repositories the
        // public API uses; the dispute store and the holiday store are the admin site's own writers.
        services.AddSingleton<IOrderRepository, DynamoOrderRepository>();
        services.AddSingleton<IPublicProfileRepository, DynamoPublicProfileRepository>();
        services.AddSingleton<IConversationRepository, DynamoConversationRepository>();
        services.AddSingleton<IDisputeAdminStore, DynamoDisputeAdminStore>();
        services.AddSingleton<IHolidayStore, DynamoHolidayStore>();

        // No payment gateway exists, so no refund sender moves money. The development switch picks the stand-in that says so differently.
        if (options.Simulators.Enabled)
        {
            services.AddSingleton<IRefundSender, SimulatedRefundSender>();
        }
        else
        {
            services.AddSingleton<IRefundSender, PendingGatewayRefundSender>();
        }

        return services;
    }
    /// <summary>
    /// Chooses how live events reach browsers: through API Gateway's WebSocket API when its
    /// management endpoint is configured, and nowhere otherwise.
    /// </summary>
    /// <remarks>
    /// The development host replaces this with an in-process hub (see <c>Program.cs</c>), which
    /// is why "nowhere" is a registered no-op rather than a missing service.
    /// </remarks>
    /// <summary>
    /// Wires the courier-tracking provider named in <c>Koleksi:Delivery:Provider</c>. <c>none</c> (the default) keeps
    /// the "buyer confirms receipt" behaviour. A name that is not registered here stops the host at start, with the
    /// list of names that are, rather than quietly running with no tracking.
    /// </summary>
    /// <remarks>
    /// Adding a provider: write its <see cref="IDeliveryWebhookProvider"/> (and an <see cref="IDeliveryTracker"/> if it
    /// can also be asked or must be told about each parcel), then add a case below. See
    /// <c>docs/delivery-providers.md</c> for the checklist.
    /// </remarks>
    private static void AddDeliveryProvider(IServiceCollection services, DeliveryOptions delivery)
    {
        switch (delivery.Provider.Trim().ToLowerInvariant())
        {
            case DeliveryOptions.None:
                services.AddSingleton<IDeliveryTracker, NotConfiguredDeliveryTracker>();
                break;

            case SignedJsonDeliveryWebhookProvider.ProviderName:
                services.AddSingleton<IDeliveryWebhookSecret, DeliveryWebhookSecret>();
                services.AddSingleton<IDeliveryWebhookProvider, SignedJsonDeliveryWebhookProvider>();
                services.AddSingleton<IDeliveryTracker, PushOnlyDeliveryTracker>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Koleksi:Delivery:Provider '{delivery.Provider}' is not a known provider. "
                    + $"Known: {DeliveryOptions.None}, {SignedJsonDeliveryWebhookProvider.ProviderName}.");
        }
    }

    private static void AddRealtimeBroadcaster(IServiceCollection services, KoleksiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Realtime.ManagementUrl))
        {
            services.AddSingleton<IRealtimeBroadcaster, NullRealtimeBroadcaster>();
            return;
        }

        services.AddSingleton<IAmazonApiGatewayManagementApi>(_ => new AmazonApiGatewayManagementApiClient(
            Bounded(new AmazonApiGatewayManagementApiConfig
            {
                ServiceURL = options.Realtime.ManagementUrl,
                AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region)
                    ? "ap-southeast-3"
                    : options.Region,
            })));

        services.AddSingleton<IRealtimeBroadcaster, ApiGatewayRealtimeBroadcaster>();
    }

    /// <summary>Per-attempt request timeout for every AWS SDK client but S3.</summary>
    internal static readonly TimeSpan SdkCallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>S3 gets a little longer: its tail latency is wider than Dynamo's.</summary>
    internal static readonly TimeSpan S3CallTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt. The SDK default is 4 (5 attempts).</summary>
    internal const int SdkMaxErrorRetry = 2;

    /// <summary>
    /// Bounds a client so a slow or unreachable dependency cannot hold a request until the
    /// Lambda's 25 s limit (API Gateway cuts at 30 s). The SDK default is a 100 s timeout and
    /// 4 retries, which once left a listing create hanging for 191 s (QA N-06).
    /// </summary>
    internal static TConfig Bounded<TConfig>(TConfig config, TimeSpan? timeout = null)
        where TConfig : ClientConfig
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Timeout = timeout ?? SdkCallTimeout;
        config.MaxErrorRetry = SdkMaxErrorRetry;
        return config;
    }

    /// <summary>
    /// Builds the DynamoDB client, honouring the development-only local endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In a deployed stage credentials come from the Lambda's execution role through the
    /// SDK's own provider chain, which is the only way this service should ever obtain
    /// them.
    /// </para>
    /// <para>
    /// Against DynamoDB Local there is no role and nothing to authenticate to, but the SDK
    /// still has to sign the request — so a developer with no AWS profile cannot start the
    /// API at all. Static placeholders remove that barrier. They are safe here because
    /// <see cref="KoleksiOptions.Validate(bool)"/> refuses a configured
    /// <see cref="DynamoOptions.ServiceUrl"/> outside development: this branch cannot be
    /// reached by a deployed stage, whatever its environment variables say.
    /// </para>
    /// </remarks>
    private static AmazonDynamoDBClient BuildDynamoClient(DynamoOptions dynamo, string region)
    {
        if (!string.IsNullOrWhiteSpace(dynamo.ServiceUrl))
        {
            return new AmazonDynamoDBClient(
                LocalDevelopmentCredentials,
                Bounded(new AmazonDynamoDBConfig
                {
                    ServiceURL = dynamo.ServiceUrl,

                    // Signing needs a region name even though the endpoint is local, and
                    // DynamoDB Local partitions its tables by it when not sharing a database.
                    AuthenticationRegion = string.IsNullOrWhiteSpace(region)
                        ? "ap-southeast-3"
                        : region,
                }));
        }

        var config = Bounded(new AmazonDynamoDBConfig());
        if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        return new AmazonDynamoDBClient(config);
    }

    /// <summary>
    /// AWS's documentation placeholders, used only against a local endpoint.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="BuildDynamoClient"/> and <see cref="BuildS3Client"/> — one pair
    /// of fake keys for every local-only client, rather than one per service. DynamoDB
    /// Local does not check that a key exists, but since version 3 it does check that it is
    /// shaped like one, and rejects anything else outright. MinIO's compose service is
    /// configured with this exact pair as its root user, so its signature check actually
    /// passes rather than being skipped.
    /// </remarks>
    private static readonly BasicAWSCredentials LocalDevelopmentCredentials =
        new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");

    /// <summary>
    /// Builds the S3 client, honouring the development-only local endpoint.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="BuildDynamoClient"/>, for the same reason — except MinIO,
    /// unlike DynamoDB Local, actually verifies the signature it is sent. It works only
    /// because the compose file that starts it is configured with this exact key pair, so
    /// the SDK's request and MinIO's check agree without either side being real AWS.
    /// <see cref="MediaOptions.Validate"/> refuses a configured <see cref="MediaOptions.ServiceUrl"/>
    /// outside development, same as Dynamo's.
    /// </remarks>
    private static AmazonS3Client BuildS3Client(KoleksiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Media.ServiceUrl))
        {
            return new AmazonS3Client(
                LocalDevelopmentCredentials,
                Bounded(new AmazonS3Config
                {
                    ServiceURL = options.Media.ServiceUrl,

                    // MinIO is addressed as host/bucket/key, not bucket.host/key — it has
                    // no DNS entry per bucket the way real S3 does.
                    ForcePathStyle = true,

                    AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region)
                        ? "ap-southeast-3"
                        : options.Region,
                }, S3CallTimeout));
        }

        var config = Bounded(new AmazonS3Config(), S3CallTimeout);
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonS3Client(config);
    }

    private static AmazonSecretsManagerClient BuildSecretsClient(KoleksiOptions options)
    {
        var config = Bounded(new AmazonSecretsManagerConfig());
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSecretsManagerClient(config);
    }

    private static AmazonSimpleNotificationServiceClient BuildSnsClient(KoleksiOptions options)
    {
        var config = Bounded(new AmazonSimpleNotificationServiceConfig());
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSimpleNotificationServiceClient(config);
    }

    private static AmazonSimpleEmailServiceV2Client BuildSesClient(KoleksiOptions options)
    {
        var config = Bounded(new AmazonSimpleEmailServiceV2Config());
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSimpleEmailServiceV2Client(config);
    }
}
