using System.Net;
using Koleksi.Api.Security;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// <c>POST /internal/sweep</c>: reachable from the internet through the gateway's catch-all, so
/// the shared secret is the only gate, and a refusal must be indistinguishable from "no such
/// route".
/// </summary>
public sealed class SweepEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string Route = "/internal/sweep";
    private const string Header = "X-Koleksi-Internal";

    [Fact]
    public async Task A_call_with_no_secret_is_a_bare_404_that_says_nothing()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(Route, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_wrong_secret_is_a_404_not_a_401_or_403()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, KoleksiApiFactory.InternalSecret + "x");

        using var response = await client.PostAsync(Route, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bearer_token_is_no_substitute_for_the_secret()
    {
        // The route is anonymous at the framework level, so a signed-in user, even staff, gets
        // nothing more than a stranger does.
        using var client = factory.CreateClientAs(TestData.StaffId, "staff.member", "staff");

        using var response = await client.PostAsync(Route, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Another_method_is_a_404_even_with_the_right_secret(string method)
    {
        // A POST-only route would answer 405, which announces that something lives here.
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), Route);
        request.Headers.Add(Header, KoleksiApiFactory.InternalSecret);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_right_secret_runs_the_sweep_and_reports_what_it_did()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);
        var auction = TestData.LiveAuction(listing, Money.FromRupiah(1_000_000), TimeSpan.FromHours(1));
        auction.PlaceBid(TestData.BidderId, TestData.BidderUsername, Money.FromRupiah(1_200_000), factory.Clock);
        factory.Listings.Seed(listing);
        factory.Auctions.Seed(auction);
        factory.Clock.Advance(TimeSpan.FromHours(2));

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route);
        request.Headers.Add(Header, KoleksiApiFactory.InternalSecret);

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("closedAuctions").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        body.GetProperty("endedWithoutSale").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("cancelledOrders").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("releasedOrders").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
        body.GetProperty("errors").GetInt32().ShouldBe(0);
        body.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(0);

        (await factory.Auctions.FindAsync(listing.Id, CancellationToken.None))!.Status.ShouldBe(AuctionStatus.Ended);
    }

    [Fact]
    public async Task Calling_it_again_is_harmless()
    {
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route);
            request.Headers.Add(Header, KoleksiApiFactory.InternalSecret);
            using var response = await client.SendAsync(request);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadJsonAsync()).GetProperty("errors").GetInt32().ShouldBe(0);
        }
    }

    // ─── The gate itself, including the case the factory cannot express ───────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void With_no_secret_configured_nobody_is_authorised_not_even_an_empty_header(string? configured)
    {
        InternalSecretGate.IsAuthorised(configured, new StringValues(configured ?? string.Empty)).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(configured, new StringValues("anything")).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(configured, StringValues.Empty).ShouldBeFalse();
    }

    [Fact]
    public void Only_a_single_exact_match_is_authorised()
    {
        const string Secret = "0123456789abcdef0123456789abcdef";

        InternalSecretGate.IsAuthorised(Secret, new StringValues(Secret)).ShouldBeTrue();
        InternalSecretGate.IsAuthorised(Secret, new StringValues(Secret + "0")).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(Secret, new StringValues(Secret[..^1])).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(Secret, new StringValues(Secret.ToUpperInvariant())).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(Secret, StringValues.Empty).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(Secret, new StringValues(string.Empty)).ShouldBeFalse();

        // Two values, even if one is right: ambiguous, so not trusted.
        InternalSecretGate.IsAuthorised(Secret, new StringValues([Secret, Secret])).ShouldBeFalse();
        InternalSecretGate.IsAuthorised(Secret, new StringValues([Secret, "other"])).ShouldBeFalse();
    }
}
