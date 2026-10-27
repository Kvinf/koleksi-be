using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>What an outsider can reach: the gate in front of the API, the headers on every answer, and the routes that exist at all.</summary>
public sealed class AdminSurfaceTests(AdminApiFactory factory) : IClassFixture<AdminApiFactory>
{
    [Theory]
    [InlineData("/api/admin/session", true)]
    [InlineData("/api/admin/audit", false)]
    [InlineData("/", false)]
    [InlineData("/api/admin/does-not-exist", false)]
    public async Task A_request_without_the_web_tiers_secret_gets_404_before_authentication(string path, bool post)
    {
        // Even with a perfectly good staff token: replayed straight at the API from anywhere but the web tier, it is
        // useless. 404 rather than 401, so a probe cannot tell a real route from a made-up one.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Tokens.Access(factory.Key));

        using var response = post ? await client.PostAsync(path, content: null) : await client.GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_wrong_secret_gets_404_too()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Koleksi-Admin-Internal", AdminApiFactory.InternalSecret + "x");
        client.DefaultRequestHeaders.Authorization = new("Bearer", Tokens.Access(factory.Key));

        using var response = await client.GetAsync("/api/admin/audit");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_health_check_is_open_and_says_nothing_about_the_system()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Every_answer_carries_the_strict_headers_even_a_refusal()
    {
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health", "/api/admin/audit", "/nope" })
        {
            using var response = await client.GetAsync(path);

            response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"], path);
            response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"], path);
            response.Headers.GetValues("X-Robots-Tag").ShouldBe(["noindex, nofollow"], path);
            response.Headers.CacheControl.ShouldNotBeNull(path);
            response.Headers.CacheControl.NoStore.ShouldBeTrue(path);
            response.Headers.CacheControl.Private.ShouldBeTrue(path);
            response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'", customMessage: path);
            response.Headers.Contains("Server").ShouldBeFalse(path);
        }
    }

    [Fact]
    public async Task No_origin_is_ever_allowed_by_cors()
    {
        // The browser never calls this API, so there is no CORS middleware at all. A cross-origin request, and a
        // preflight for one, get no permission headers whatever the origin says.
        using var client = factory.CreateWebTierClientWithToken(Tokens.Access(factory.Key));

        using var simple = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit");
        simple.Headers.Add("Origin", "https://evil.example");
        using var simpleResponse = await client.SendAsync(simple);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/admin/session");
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        using var preflightResponse = await client.SendAsync(preflight);

        foreach (var response in new[] { simpleResponse, preflightResponse })
        {
            response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
            response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
        }

        preflightResponse.IsSuccessStatusCode.ShouldBeFalse();
    }

    [Fact]
    public void Every_route_but_the_health_check_requires_a_named_capability()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.ShouldNotBeEmpty();

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText ?? string.Empty;
            var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (route == "/health")
            {
                anonymous.ShouldBeTrue("the liveness check is the one anonymous route");
                continue;
            }

            anonymous.ShouldBeFalse($"{route} must not be anonymous");
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(data => !string.IsNullOrEmpty(data.Policy))
                .ShouldBeTrue($"{route} must name the capability it needs");
        }
    }

    [Fact]
    public void Nothing_but_the_admin_namespace_and_the_health_check_is_routed()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldAllBe(route => route == "/health" || route.StartsWith("/api/admin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_development_sign_in_does_not_exist_outside_development()
    {
        // Debug builds contain the local staff identity; the Testing environment must not switch it on. (A Release build
        // does not contain it at all, which scripts/verify-release-has-no-local-staff.ps1 checks on the published output.)
        // Asked as a signed-in staff member, so the answer separates the two cases: a mapped (anonymous) sign-in route
        // would answer 401 for these credentials, while a route that does not exist answers 404.
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsJsonAsyncSafe("/dev/staff/sign-in");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncSafe(this HttpClient client, string path) =>
        client.PostAsync(path, new StringContent("""{"username":"x","password":"y"}""", System.Text.Encoding.UTF8, "application/json"));
}
