using System.Net;
using System.Net.Http.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// A value that cannot be an id or a cursor this service issued is the caller's mistake: it must never reach a DynamoDB key
/// and come back as an unhandled 500 (QA P-01, P-02).
/// </summary>
public sealed class MalformedIdAndCursorTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly string TooLong = new('A', 3000);

    [Theory]
    [InlineData("GET", "/api/orders/{0}")]
    [InlineData("POST", "/api/orders/{0}/cancel")]
    [InlineData("POST", "/api/orders/{0}/release")]
    [InlineData("POST", "/api/orders/{0}/dispute")]
    [InlineData("POST", "/api/orders/{0}/shipment")]
    [InlineData("GET", "/api/orders/{0}/review")]
    [InlineData("GET", "/api/offers/{0}")]
    [InlineData("POST", "/api/offers/{0}/accept")]
    [InlineData("GET", "/api/conversations/{0}")]
    [InlineData("POST", "/api/conversations/{0}/messages")]
    [InlineData("GET", "/api/listings/{0}")]
    [InlineData("PUT", "/api/listings/{0}")]
    [InlineData("POST", "/api/listings/{0}/archive")]
    [InlineData("POST", "/api/listings/{0}/offers")]
    [InlineData("GET", "/api/listings/{0}/conversation")]
    [InlineData("POST", "/api/auctions/{0}/bids")]
    [InlineData("DELETE", "/api/cart/items/{0}")]
    public async Task An_id_that_cannot_be_ours_is_a_404_not_a_500(string method, string template)
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        foreach (var id in new[] { TooLong, "short", "not-an-id-but-26-chars-long!", new string('0', 25), new string('0', 27) })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), string.Format(null, template, id));
            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await client.SendAsync(request);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {template} with a {id.Length}-character id");
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("request.not_found");
        }
    }

    [Fact]
    public async Task A_very_long_id_on_the_watchlist_is_refused_by_its_own_handler()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var save = await client.PutAsync($"/api/watchlist/{TooLong}", null);
        using var remove = await client.DeleteAsync($"/api/watchlist/{TooLong}");

        save.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        remove.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_well_formed_id_that_names_nothing_keeps_its_own_answer()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.GetAsync($"/api/orders/{EntityId.New()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_found");
    }

    [Fact]
    public async Task A_lower_case_id_is_still_an_id()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.GetAsync($"/api/orders/{EntityId.New().ToLowerInvariant()}");

        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_found");
    }

    [Fact]
    public async Task An_anonymous_caller_still_gets_the_401_not_the_id_check()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/orders/{TooLong}");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_cursor_the_store_cannot_resume_from_is_a_400_not_a_500()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        factory.Orders.FailWith = new InvalidCursorException("The page cursor is not valid.", new InvalidOperationException("starting key"));

        try
        {
            using var response = await client.GetAsync("/api/orders/mine?cursor=Zm9yZ2Vk");

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("request.invalid_cursor");
        }
        finally
        {
            factory.Orders.FailWith = null;
        }
    }
}
