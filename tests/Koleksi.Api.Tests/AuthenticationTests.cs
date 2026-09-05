using System.Net;
using System.Net.Http.Headers;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// What the API accepts as proof of identity.
/// </summary>
/// <remarks>
/// Every case here is a token that is <b>correctly signed and structurally valid</b> and
/// must still be refused. That is the interesting half of token validation: a garbage
/// string is rejected by accident, whereas a real token minted for the wrong client, or
/// an id token used as a credential, is rejected only because someone configured the
/// handler to notice.
/// </remarks>
public sealed class AuthenticationTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task An_anonymous_bid_is_refused()
    {
        var auction = Seed();
        using var client = factory.CreateClient();

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_access_token_places_a_bid_as_its_own_subject()
    {
        var auction = Seed();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The bidder is taken from the validated token, never from the body — so this
        // also proves the "sub" claim survives onto ICurrentUser rather than being
        // renamed by inbound claim mapping, which would silently anonymise every caller.
        var bids = factory.Auctions.RecordedBids(auction);
        bids.ShouldNotBeEmpty();
        bids[^1].BidderId.ShouldBe(TestData.BidderId);
    }

    [Fact]
    public async Task An_id_token_is_not_a_credential()
    {
        var auction = Seed();
        var token = TestTokens.Access(factory.SigningKey, new TokenOptions { TokenUse = "id" });
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_minted_for_another_app_client_is_refused()
    {
        // Same pool, same signing key, same issuer — only the client differs. Without the
        // explicit client_id check this token would be indistinguishable from a valid one,
        // because Cognito access tokens carry no "aud" to validate.
        var auction = Seed();
        var token = TestTokens.Access(
            factory.SigningKey, new TokenOptions { ClientId = "some-other-app-client" });
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_from_another_issuer_is_refused()
    {
        var auction = Seed();
        var token = TestTokens.Access(factory.SigningKey, new TokenOptions
        {
            Issuer = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_attacker",
        });
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_that_expired_ninety_seconds_ago_is_refused()
    {
        // Ninety seconds is the case that matters. The framework default tolerates five
        // minutes of clock skew, which would accept this; Program cuts that to thirty
        // seconds precisely so a stolen token stops working when it says it does.
        var auction = Seed();
        var token = TestTokens.Access(factory.SigningKey, new TokenOptions
        {
            IssuedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(31),
            Lifetime = TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(30),
        });
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_signed_by_a_key_we_do_not_publish_is_refused()
    {
        var auction = Seed();
        using var forgedKey = new TestSigningKey("koleksi-test-key");
        var token = TestTokens.Access(forgedKey);
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_hmac_token_signed_with_the_public_key_is_refused()
    {
        // The classic algorithm-confusion attack. Everything the attacker needs is public.
        var auction = Seed();
        var token = TestTokens.HmacSignedWithThePublicKey(factory.SigningKey);
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_malformed_bearer_value_is_refused()
    {
        var auction = Seed();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.token");

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_rejected_request_still_carries_the_security_headers()
    {
        // The 401 is produced by the authentication middleware, upstream of any endpoint.
        // If the headers were applied per-endpoint they would be missing exactly here.
        var auction = Seed();
        using var client = factory.CreateClient();

        using var response = await client.PostBidAsync(auction, 1_500_000);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    /// <summary>Seeds a live auction and returns its listing id.</summary>
    private string Seed()
    {
        var listing = TestData.ActiveListing();
        var auction = TestData.LiveAuction(listing);

        factory.Listings.Seed(listing);
        factory.Auctions.Seed(auction);

        return listing.Id;
    }
}
