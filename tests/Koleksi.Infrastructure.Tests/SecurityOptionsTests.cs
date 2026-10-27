using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>The shared secret that lets the web tier name the visitor an API request is for.</summary>
public sealed class SecurityOptionsTests
{
    private static KoleksiOptions WithSecret(string? secret) => new()
    {
        Region = "ap-southeast-3",
        Cognito = new CognitoOptions { UserPoolId = "ap-southeast-3_koleksitest", ClientId = "1testclient0koleksi" },
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
        Security = new SecurityOptions { InternalSecret = secret },
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_secret_means_the_feature_is_off_and_the_configuration_is_valid(string? secret)
    {
        var options = WithSecret(secret);

        Should.NotThrow(() => options.Validate());
        options.Security.HasInternalSecret.ShouldBeFalse();
    }

    [Fact]
    public void A_secret_of_at_least_thirty_two_characters_is_accepted()
    {
        var options = WithSecret(new string('s', SecurityOptions.MinInternalSecretLength));

        Should.NotThrow(() => options.Validate());
        options.Security.HasInternalSecret.ShouldBeTrue();
    }

    [Fact]
    public void A_short_secret_fails_startup_without_echoing_it()
    {
        const string tooShort = "hunter2-but-secret";

        var failure = Should.Throw<InvalidOperationException>(() => WithSecret(tooShort).Validate());

        failure.Message.ShouldContain("Koleksi:Security:InternalSecret");
        failure.Message.ShouldNotContain(tooShort);
    }
}
