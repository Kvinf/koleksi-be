using System.Net;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// <c>POST /internal/order-sweep-backfill</c> (QA D-12): same secret and the same quiet refusal as
/// the sweep and the suggestion backfill.
/// </summary>
public sealed class OrderSweepBackfillEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string Route = "/internal/order-sweep-backfill";
    private const string Header = "X-Koleksi-Internal";

    [Fact]
    public async Task A_call_with_no_secret_or_a_wrong_one_is_refused_like_an_unknown_route()
    {
        using var client = factory.CreateClient();
        var callsBefore = factory.OrderSweepBackfill.Calls; // the fixture is shared with the test that uses the right secret
        using var none = await client.PostAsync(Route, content: null);
        none.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await none.Content.ReadAsStringAsync()).ShouldBeEmpty();

        using var wrong = new HttpRequestMessage(HttpMethod.Post, Route);
        wrong.Headers.Add(Header, KoleksiApiFactory.InternalSecret + "x");
        using var wrongResponse = await client.SendAsync(wrong);
        wrongResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        factory.OrderSweepBackfill.Calls.ShouldBe(callsBefore);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Another_method_is_refused_even_with_the_right_secret(string method)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Route);
        request.Headers.Add(Header, KoleksiApiFactory.InternalSecret);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_right_secret_runs_one_bounded_step_and_passes_the_cursor_through()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route + "?cursor=abc");
        request.Headers.Add(Header, KoleksiApiFactory.InternalSecret);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("examined").GetInt32().ShouldBe(200);
        body.GetProperty("updated").GetInt32().ShouldBe(3);
        body.GetProperty("nextCursor").GetString().ShouldBe("next-page");
        factory.OrderSweepBackfill.LastCursor.ShouldBe("abc");
        factory.OrderSweepBackfill.LastLimit.ShouldBeInRange(1, 500);
    }
}
