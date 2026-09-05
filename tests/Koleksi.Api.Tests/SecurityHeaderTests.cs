using System.Net;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The response headers that constrain what a browser will do with our output.
/// </summary>
/// <remarks>
/// The interesting assertion is not that a 200 carries them — it is that a 404, a 401 and
/// a 500 carry them too. Those responses are produced upstream of any endpoint, and a
/// header applied per-endpoint would be missing from exactly the responses most likely to
/// echo something a caller supplied.
/// </remarks>
public sealed class SecurityHeaderTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/catalog/categories")]
    [InlineData("/api/catalog/listings/does-not-exist")]
    [InlineData("/api/does/not/route/anywhere")]
    public async Task Every_response_carries_them(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.GetValues("X-Frame-Options").ShouldContain("DENY");
        response.Headers.GetValues("Referrer-Policy").ShouldContain("strict-origin-when-cross-origin");

        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        policy.ShouldContain("default-src 'none'");
        policy.ShouldContain("frame-ancestors 'none'");
        policy.ShouldContain("base-uri 'none'");
        policy.ShouldContain("form-action 'none'");

        var permissions = response.Headers.GetValues("Permissions-Policy").Single();
        permissions.ShouldContain("camera=()");
        permissions.ShouldContain("microphone=()");
        permissions.ShouldContain("geolocation=()");
        permissions.ShouldContain("payment=()");
    }

    [Fact]
    public async Task The_server_product_is_not_advertised()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Responses_default_to_not_being_stored()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task An_endpoint_may_opt_public_data_back_into_a_shared_cache()
    {
        // The default has to be overridable or every public page would be uncacheable;
        // what matters is that opting in is an explicit line in the endpoint, and that
        // the middleware does not stamp over it.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cacheControl = response.Headers.CacheControl.ShouldNotBeNull();
        cacheControl.Public.ShouldBeTrue();
        cacheControl.NoStore.ShouldBeFalse();
    }

    [Fact]
    public async Task The_health_check_answers_without_touching_a_dependency()
    {
        // Liveness only, on purpose. A health check that probes DynamoDB turns a
        // downstream blip into a load balancer pulling every healthy instance.
        factory.Listings.FailWith = new InvalidOperationException("storage is down");
        factory.SearchIndex.FailWith = new InvalidOperationException("storage is down");

        try
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/health");

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            factory.Listings.FailWith = null;
            factory.SearchIndex.FailWith = null;
        }
    }
}
