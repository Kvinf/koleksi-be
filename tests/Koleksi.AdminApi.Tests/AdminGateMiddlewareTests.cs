using Koleksi.AdminApi.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>
/// The gate on its own, including the case the hosted suite cannot reach: a developer's machine, where no shared secret is
/// configured. That case once stopped the API from starting at all.
/// </summary>
public sealed class AdminGateMiddlewareTests
{
    private const string Secret = "gate-secret-0123456789abcdef-0123456789";

    private static async Task<(int Status, bool Reached)> Send(string? configuredSecret, string path, string? presented)
    {
        var reached = false;
        var middleware = new AdminGateMiddleware(_ => { reached = true; return Task.CompletedTask; }, configuredSecret);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (presented is not null)
        {
            context.Request.Headers[AdminGateMiddleware.HeaderName] = presented;
        }

        await middleware.InvokeAsync(context);
        return (context.Response.StatusCode, reached);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task With_no_secret_configured_the_gate_is_off(string? configured)
    {
        var (status, reached) = await Send(configured, "/api/admin/audit", presented: null);

        reached.ShouldBeTrue();
        status.ShouldBe(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task With_a_secret_configured_a_request_without_it_never_reaches_the_pipeline()
    {
        var (status, reached) = await Send(Secret, "/api/admin/audit", presented: null);

        reached.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData(Secret + "x")]
    [InlineData("gate-secret-0123456789abcdef-012345678")]
    public async Task A_wrong_or_partial_secret_is_turned_away(string presented)
    {
        var (status, reached) = await Send(Secret, "/api/admin/audit", presented);

        reached.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task The_right_secret_passes()
    {
        var (_, reached) = await Send(Secret, "/api/admin/audit", Secret);

        reached.ShouldBeTrue();
    }

    [Fact]
    public async Task The_secret_given_twice_is_ambiguous_and_refused()
    {
        var reached = false;
        var middleware = new AdminGateMiddleware(_ => { reached = true; return Task.CompletedTask; }, Secret);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/audit";
        context.Request.Headers[AdminGateMiddleware.HeaderName] = new Microsoft.Extensions.Primitives.StringValues([Secret, Secret]);

        await middleware.InvokeAsync(context);

        reached.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/HEALTH")]
    public async Task The_health_check_is_exempt_because_it_says_nothing(string path)
    {
        var (_, reached) = await Send(Secret, path, presented: null);

        reached.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/health/")]
    [InlineData("/healthz")]
    [InlineData("/health/details")]
    [InlineData("/api/admin/health")]
    public async Task Only_the_exact_health_path_is_exempt(string path)
    {
        var (status, reached) = await Send(Secret, path, presented: null);

        reached.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
