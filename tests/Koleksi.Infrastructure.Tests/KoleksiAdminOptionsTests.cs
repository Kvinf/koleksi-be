using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>The admin API's configuration: strict on a deployed stage, relaxed only on a developer's machine.</summary>
public sealed class KoleksiAdminOptionsTests
{
    private const string Secret = "0123456789abcdef0123456789abcdef";

    private static KoleksiAdminOptions Deployed(
        string? secret = Secret, string stage = "dev", string pool = "ap-southeast-3_staffpool", string client = "1staffclient") =>
        new()
        {
            Region = "ap-southeast-3",
            Stage = stage,
            StaffPool = new CognitoOptions { UserPoolId = pool, ClientId = client },
            Dynamo = new DynamoOptions { TableName = "koleksi-dev" },
            Security = new SecurityOptions { InternalSecret = secret },
        };

    [Fact]
    public void A_complete_deployed_configuration_is_valid()
    {
        var options = Deployed();

        Should.NotThrow(() => options.Validate(allowLocalEndpoints: false));
        options.IsDeployed.ShouldBeTrue();
    }

    [Fact]
    public void The_token_issuer_is_derived_from_the_staff_pool_not_configured()
    {
        var options = Deployed();
        options.Validate(allowLocalEndpoints: false);

        options.StaffPool.Authority.ShouldBe("https://cognito-idp.ap-southeast-3.amazonaws.com/ap-southeast-3_staffpool");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_deployed_stage_must_have_the_shared_secret(string? secret)
    {
        var failure = Should.Throw<InvalidOperationException>(() => Deployed(secret).Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("Security:InternalSecret");
    }

    [Fact]
    public void A_short_secret_is_refused_without_echoing_it()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Deployed("short").Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("at least");
        failure.Message.ShouldNotContain("short'");
    }

    [Fact]
    public void A_deployed_stage_must_name_its_stage()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Deployed(stage: "").Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("Stage is required");
    }

    [Fact]
    public void A_developers_machine_may_leave_out_the_secret_and_the_stage()
    {
        var options = Deployed(secret: null, stage: "");

        Should.NotThrow(() => options.Validate(allowLocalEndpoints: true));
        options.IsDeployed.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_pool_is_reported_together_with_every_other_problem()
    {
        var options = new KoleksiAdminOptions { Region = "ap-southeast-3", Stage = "dev", Security = new SecurityOptions { InternalSecret = Secret } };

        var failure = Should.Throw<InvalidOperationException>(() => options.Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("StaffPool:UserPoolId");
        failure.Message.ShouldContain("StaffPool:ClientId");
        failure.Message.ShouldContain("Dynamo:TableName");
    }

    [Fact]
    public void A_pool_id_without_a_region_part_is_refused()
    {
        var failure = Should.Throw<InvalidOperationException>(() => Deployed(pool: "nopool").Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("region_poolid");
    }

    [Fact]
    public void A_local_dynamo_endpoint_is_refused_on_a_deployed_stage()
    {
        var options = new KoleksiAdminOptions
        {
            Region = "ap-southeast-3",
            Stage = "dev",
            StaffPool = new CognitoOptions { UserPoolId = "ap-southeast-3_staffpool", ClientId = "1staffclient" },
            Dynamo = new DynamoOptions { TableName = "koleksi-dev", ServiceUrl = "http://evil.example:8000" },
            Security = new SecurityOptions { InternalSecret = Secret },
        };

        var failure = Should.Throw<InvalidOperationException>(() => options.Validate(allowLocalEndpoints: false));

        failure.Message.ShouldContain("development-only");
    }
}
