using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Startup configuration validation.
/// </summary>
/// <remarks>
/// Every rule here exists so a misconfigured deployment fails to start rather than
/// starting in a weakened state. A blank Cognito authority discovered lazily on the first
/// request means an API that accepted traffic while unable to validate a single token,
/// and identity documents pointed at the public bucket is a breach report rather than a
/// stack trace.
/// </remarks>
public sealed class KoleksiOptionsTests
{
    [Fact]
    public void A_complete_configuration_is_accepted()
    {
        Should.NotThrow(() => Valid().Validate());
    }

    [Fact]
    public void The_cognito_authority_is_derived_from_the_pool_rather_than_configured()
    {
        // Derived so it cannot drift out of step with the pool id, and so nobody can point
        // the API at an issuer we do not control by editing one setting.
        var options = Valid();
        options.Validate();

        options.Cognito.Authority
            .ShouldBe("https://cognito-idp.ap-southeast-3.amazonaws.com/ap-southeast-3_koleksitest");
    }

    [Fact]
    public void A_cognito_region_of_its_own_overrides_the_default()
    {
        var valid = Valid();
        var options = new KoleksiOptions
        {
            Region = "ap-southeast-3",
            Cognito = new CognitoOptions
            {
                Region = "ap-southeast-1",
                UserPoolId = "ap-southeast-1_elsewhere",
                ClientId = "1testclient0koleksi",
            },
            Dynamo = valid.Dynamo,
            Media = valid.Media,
            Email = valid.Email,
            Payment = valid.Payment,
            Cors = valid.Cors,
        };

        options.Validate();

        options.Cognito.Authority.ShouldStartWith("https://cognito-idp.ap-southeast-1.amazonaws.com/");
    }

    [Fact]
    public void A_local_dynamodb_endpoint_is_refused_in_a_deployed_stage()
    {
        // Otherwise one environment variable points production at an endpoint of someone
        // else's choosing, and the marketplace's reads and writes go there.
        var options = With(dynamo: new DynamoOptions
        {
            TableName = "koleksi-prod",
            ServiceUrl = "http://192.0.2.10:8000",
        });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("development-only override");
    }

    [Fact]
    public void A_local_dynamodb_endpoint_is_permitted_in_development()
    {
        var options = With(dynamo: new DynamoOptions
        {
            TableName = "koleksi-dev",
            ServiceUrl = "http://localhost:8000",
        });

        Should.NotThrow(() => options.Validate(allowLocalEndpoints: true));
    }

    [Theory]
    [InlineData("localhost:8000")]
    [InlineData("8000")]
    [InlineData("file:///c:/dynamo")]
    public void A_local_dynamodb_endpoint_must_still_be_an_http_url(string serviceUrl)
    {
        // "localhost:8000" parses as an absolute URI whose *scheme* is "localhost", so a
        // check for absoluteness alone accepts a bare host and port and the mistake only
        // surfaces later, as a connection error naming nothing useful.
        var options = With(dynamo: new DynamoOptions
        {
            TableName = "koleksi-dev",
            ServiceUrl = serviceUrl,
        });

        Should.Throw<InvalidOperationException>(() => options.Validate(allowLocalEndpoints: true))
            .Message.ShouldContain("absolute http or https URL");
    }

    [Fact]
    public void Identity_documents_may_not_share_the_public_bucket()
    {
        var options = With(media: new MediaOptions
        {
            PublicBucket = "koleksi-media",
            PrivateBucket = "koleksi-media",
            CdnBaseUrl = "https://media.koleksi.test",
        });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("identity documents must not live in a CDN-backed bucket");
    }

    [Fact]
    public void The_payment_key_may_only_be_named_by_arn()
    {
        // The webhook signing key is the one true secret in the system. Allowing it inline
        // would put it in a CloudFormation template, a build artefact, and a log line.
        var options = With(payment: new PaymentOptions
        {
            BaseUrl = "https://payments.koleksi.test",
            ClientId = "BRN-TEST-0001",
            SecretArn = "s3cr3t-value-pasted-here",
            ReturnUrl = "https://koleksi.test/checkout/selesai",
        });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("must never be configured inline");
    }

    [Fact]
    public void A_wildcard_origin_is_refused_because_the_policy_allows_credentials()
    {
        var options = With(cors: new CorsOptions { AllowedOrigins = ["*"] });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("must not contain '*'");
    }

    [Fact]
    public void An_origin_with_a_path_is_refused_rather_than_silently_never_matching()
    {
        // An origin is scheme, host and port. A trailing path never matches, which
        // presents as "CORS is broken" long after the deployment that caused it.
        var options = With(cors: new CorsOptions
        {
            AllowedOrigins = ["https://koleksi.test/app"],
        });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("must be a bare origin");
    }

    [Fact]
    public void An_empty_origin_list_is_refused()
    {
        var options = With(cors: new CorsOptions { AllowedOrigins = [] });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("at least one origin");
    }

    [Theory]
    [InlineData("http://payments.koleksi.test")]
    [InlineData("payments.koleksi.test")]
    public void A_payment_endpoint_that_is_not_https_is_refused(string baseUrl)
    {
        // Plaintext would mean redirecting a paying buyer over a channel any intermediary
        // can rewrite.
        var options = With(payment: new PaymentOptions
        {
            BaseUrl = baseUrl,
            ClientId = "BRN-TEST-0001",
            SecretArn = "arn:aws:secretsmanager:ap-southeast-3:000000000000:secret:koleksi",
            ReturnUrl = "https://koleksi.test/checkout/selesai",
        });

        Should.Throw<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void An_upload_grant_that_never_expires_is_refused()
    {
        var options = With(media: new MediaOptions
        {
            PublicBucket = "koleksi-media",
            PrivateBucket = "koleksi-identity",
            CdnBaseUrl = "https://media.koleksi.test",
            UploadGrantLifetime = TimeSpan.FromDays(1),
        });

        Should.Throw<InvalidOperationException>(options.Validate)
            .Message.ShouldContain("UploadGrantLifetime");
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        // Reporting them one at a time turns a five-minute fix into five cold starts.
        var options = new KoleksiOptions();

        var message = Should.Throw<InvalidOperationException>(options.Validate).Message;

        message.ShouldContain("Cognito:UserPoolId");
        message.ShouldContain("Dynamo:TableName");
        message.ShouldContain("Media:PublicBucket");
        message.ShouldContain("Payment:SecretArn");
        message.ShouldContain("Cors:AllowedOrigins");
        message.ShouldContain("docs/configuration.md");
    }

    private static KoleksiOptions Valid() => new()
    {
        Region = "ap-southeast-3",
        Cognito = new CognitoOptions
        {
            UserPoolId = "ap-southeast-3_koleksitest",
            ClientId = "1testclient0koleksi",
        },
        Dynamo = new DynamoOptions { TableName = "koleksi-test" },
        Media = new MediaOptions
        {
            PublicBucket = "koleksi-test-media",
            PrivateBucket = "koleksi-test-identity",
            CdnBaseUrl = "https://media.koleksi.test",
        },
        Email = new EmailOptions { FromAddress = "verifikasi@koleksi.test" },
        Payment = new PaymentOptions
        {
            BaseUrl = "https://payments.koleksi.test",
            ClientId = "BRN-TEST-0001",
            SecretArn = "arn:aws:secretsmanager:ap-southeast-3:000000000000:secret:koleksi-test",
            ReturnUrl = "https://koleksi.test/checkout/selesai",
        },
        Cors = new CorsOptions { AllowedOrigins = ["https://koleksi.test"] },
    };

    /// <summary>A valid configuration with one section swapped for a broken one.</summary>
    private static KoleksiOptions With(
        MediaOptions? media = null,
        PaymentOptions? payment = null,
        CorsOptions? cors = null,
        DynamoOptions? dynamo = null)
    {
        var valid = Valid();

        return new KoleksiOptions
        {
            Region = valid.Region,
            Cognito = valid.Cognito,
            Dynamo = dynamo ?? valid.Dynamo,
            Media = media ?? valid.Media,
            Email = valid.Email,
            Payment = payment ?? valid.Payment,
            Cors = cors ?? valid.Cors,
        };
    }
}
