using System.Net;
using System.Text;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// What a failure tells the caller.
/// </summary>
/// <remarks>
/// The split the exception handler exists to make: a <c>DomainException</c> is a rule the
/// caller broke, so its code is safe and useful to return; anything else is our fault and
/// its message may carry a connection string, a bucket name, or an internal hostname, so
/// the caller gets a correlation id and nothing else.
/// </remarks>
public sealed class ErrorResponseTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string LeakyMessage =
        "Connection to koleksi-prod.aabbcc.ap-southeast-3.dynamodb.internal refused (secret=hunter2)";

    [Fact]
    public async Task An_internal_failure_says_nothing_about_itself()
    {
        factory.SearchIndex.FailWith = new InvalidOperationException(LeakyMessage);

        try
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/catalog/browse");

            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

            var text = await response.ReadTextAsync();
            text.ShouldNotContain("hunter2");
            text.ShouldNotContain("dynamodb.internal");
            text.ShouldNotContain(nameof(InvalidOperationException));

            var body = await response.ReadJsonAsync();
            body.GetProperty("code").GetString().ShouldBe("server.error");

            // The one thing a user can quote to support, and the only thing that ties
            // their screenshot to the log entry holding the real detail.
            body.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            factory.SearchIndex.FailWith = null;
        }
    }

    [Fact]
    public async Task An_internal_failure_still_carries_the_security_headers()
    {
        factory.SearchIndex.FailWith = new InvalidOperationException(LeakyMessage);

        try
        {
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/catalog/browse");

            response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
            response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        }
        finally
        {
            factory.SearchIndex.FailWith = null;
        }
    }

    [Fact]
    public async Task A_broken_rule_returns_its_code_so_the_frontend_can_translate_it()
    {
        // The wire contract is the stable code, not the English sentence beside it. The
        // frontend ships copy in Indonesian and English and switches on this.
        var listing = TestData.ActiveListing(format: ListingFormat.Auction);
        var auction = TestData.LiveAuction(listing);
        factory.Listings.Seed(listing);
        factory.Auctions.Seed(auction);

        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostBidAsync(listing.Id, 5_000_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auction.seller_cannot_bid");
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request_not_a_crash()
    {
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var content = new StringContent("{ this is not json", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(
            $"/api/auctions/{TestData.UnknownListingId}/bids", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadTextAsync()).ShouldNotContain("System.Text.Json");
    }
}
