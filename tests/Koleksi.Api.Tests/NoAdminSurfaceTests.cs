using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The public API contains nothing of the admin site. Not a hidden route, not a route behind the <c>staff</c> group:
/// no route at all, so there is nothing for the public to find or probe.
/// </summary>
public sealed class NoAdminSurfaceTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public void No_public_route_mentions_admin()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldNotContain(route => route.Contains("admin", StringComparison.OrdinalIgnoreCase));
        routes.ShouldNotContain(route => route.Contains("/staff", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/api/admin/session")]
    [InlineData("/api/admin/audit")]
    [InlineData("/dev/staff/sign-in")]
    public async Task Probing_for_admin_paths_finds_nothing(string path)
    {
        // Signed in as a staff member of the public pool (the old `staff` group), the strongest caller the public API
        // knows. It still finds no such route: the admin site does not exist here.
        using var client = factory.CreateClientAs("01JSTAFFAAAAAAAAAAAAAAAAAA", "someone", "staff");

        using var response = await client.GetAsync(path);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
