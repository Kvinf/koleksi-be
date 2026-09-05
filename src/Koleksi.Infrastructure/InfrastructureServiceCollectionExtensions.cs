using Amazon;
using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SimpleEmailV2;
using Amazon.SimpleNotificationService;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Identity;
using Koleksi.Infrastructure.Media;
using Koleksi.Infrastructure.Notifications;
using Koleksi.Infrastructure.Payments;
using Koleksi.Infrastructure.Persistence;
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

        services.AddSingleton<IAmazonDynamoDB>(_ => BuildDynamoClient(options));
        services.AddSingleton<IAmazonS3>(_ => BuildS3Client(options));
        services.AddSingleton<IAmazonSecretsManager>(_ => BuildSecretsClient(options));
        services.AddSingleton<IAmazonSimpleNotificationService>(_ => BuildSnsClient(options));
        services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => BuildSesClient(options));

        services.AddSingleton<IListingRepository, DynamoListingRepository>();
        services.AddSingleton<IAuctionRepository, DynamoAuctionRepository>();
        services.AddSingleton<IOfferRepository, DynamoOfferRepository>();
        services.AddSingleton<IOrderRepository, DynamoOrderRepository>();
        services.AddSingleton<ICartRepository, DynamoCartRepository>();
        services.AddSingleton<IListingSearchIndex, DynamoListingSearchIndex>();
        services.AddSingleton<ICategoryRepository, SeededCategoryRepository>();
        services.AddSingleton<IIdempotencyStore, DynamoIdempotencyStore>();
        services.AddSingleton<IProfileRepository, DynamoProfileRepository>();
        services.AddSingleton<IPhoneVerificationService, SnsPhoneVerificationService>();
        services.AddSingleton<IEmailVerificationService, SesEmailVerificationService>();

        services.AddSingleton<IMediaStorage, S3MediaStorage>();

        // Anonymous credentials on purpose: sign-in and sign-up are unauthenticated
        // Cognito APIs, so the identity path is given no AWS authority whatsoever. A
        // compromise of this service inherits nothing it could do to the user pool.
        services.AddSingleton<IAmazonCognitoIdentityProvider>(_ => new AmazonCognitoIdentityProviderClient(
            new AnonymousAWSCredentials(),
            new AmazonCognitoIdentityProviderConfig
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.Cognito.ResolvedRegion),
            }));

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

        return services;
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
    private static AmazonDynamoDBClient BuildDynamoClient(KoleksiOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Dynamo.ServiceUrl))
        {
            return new AmazonDynamoDBClient(
                LocalDevelopmentCredentials,
                new AmazonDynamoDBConfig
                {
                    ServiceURL = options.Dynamo.ServiceUrl,

                    // Signing needs a region name even though the endpoint is local, and
                    // DynamoDB Local partitions its tables by it when not sharing a database.
                    AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region)
                        ? "ap-southeast-3"
                        : options.Region,
                });
        }

        var config = new AmazonDynamoDBConfig();
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
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
                new AmazonS3Config
                {
                    ServiceURL = options.Media.ServiceUrl,

                    // MinIO is addressed as host/bucket/key, not bucket.host/key — it has
                    // no DNS entry per bucket the way real S3 does.
                    ForcePathStyle = true,

                    AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region)
                        ? "ap-southeast-3"
                        : options.Region,
                });
        }

        var config = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonS3Client(config);
    }

    private static AmazonSecretsManagerClient BuildSecretsClient(KoleksiOptions options)
    {
        var config = new AmazonSecretsManagerConfig();
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSecretsManagerClient(config);
    }

    private static AmazonSimpleNotificationServiceClient BuildSnsClient(KoleksiOptions options)
    {
        var config = new AmazonSimpleNotificationServiceConfig();
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSimpleNotificationServiceClient(config);
    }

    private static AmazonSimpleEmailServiceV2Client BuildSesClient(KoleksiOptions options)
    {
        var config = new AmazonSimpleEmailServiceV2Config();
        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return new AmazonSimpleEmailServiceV2Client(config);
    }
}
