using System.Net.Http;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Which browser origins may call this API.
/// </summary>
/// <remarks>
/// The policy allows credentials, which is what makes the allowlist load-bearing rather
/// than cosmetic: an origin that gets through can make authenticated calls on a signed-in
/// user's behalf from a page we do not control.
/// </remarks>
public sealed class CorsTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string HostileOrigin = "https://koleksi.id.attacker.test";

    [Fact]
    public async Task The_configured_origin_is_allowed_with_credentials()
    {
        using var client = factory.CreateClient();
        using var request = Preflight(KoleksiApiFactory.AllowedOrigin);

        using var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin")
            .ShouldContain(KoleksiApiFactory.AllowedOrigin);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldContain("true");
    }

    [Fact]
    public async Task An_origin_that_is_not_on_the_list_is_not_reflected()
    {
        using var client = factory.CreateClient();
        using var request = Preflight(HostileOrigin);

        using var response = await client.SendAsync(request);

        // Absence is the whole mechanism. There is no error status to check for: the
        // browser refuses the response because we declined to name the origin.
        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task A_lookalike_origin_does_not_match_by_prefix()
    {
        // "https://koleksi.test.attacker.test" starts with the allowed origin. Matching on
        // anything other than the whole origin would hand an attacker a domain they can
        // register today.
        using var client = factory.CreateClient();
        using var request = Preflight($"{KoleksiApiFactory.AllowedOrigin}.attacker.test");

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task A_simple_request_from_a_hostile_origin_is_not_marked_readable()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/categories");
        request.Headers.Add("Origin", HostileOrigin);

        using var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task The_allowed_origin_is_never_a_wildcard()
    {
        // Configuration validation refuses "*" at startup, because a wildcard paired with
        // credentials is every origin on the internet acting as the signed-in user. This
        // asserts the same thing from the outside.
        using var client = factory.CreateClient();
        using var request = Preflight(KoleksiApiFactory.AllowedOrigin);

        using var response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldNotContain("*");
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/catalog/categories");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }
}
