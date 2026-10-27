using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The web tier tells the API which visitor a request is for, so address-keyed rate limits are
/// per visitor rather than one bucket for the web tier's own egress address. The header is only
/// believed when it arrives with the shared secret.
/// </summary>
/// <remarks>
/// Sign-in is the anonymous endpoint with the tightest address-keyed bucket (ten attempts, then
/// five a minute), so it is the one that shows the difference. Every request here is refused by
/// the endpoint itself — the limiter has counted it by then, which is all these tests measure.
/// </remarks>
public sealed class TrustedClientIpTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    /// <summary>Burst of ten, so fifteen attempts from one partition must hit the wall.</summary>
    private const int Attempts = 15;

    private static async Task<List<HttpStatusCode>> SignInAttemptsAsync(
        HttpClient client, Func<int, (string? Secret, string? ClientIp)> headersFor)
    {
        var statuses = new List<HttpStatusCode>(Attempts);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "", password = "" }),
            };

            var (secret, clientIp) = headersFor(attempt);
            if (secret is not null)
            {
                request.Headers.Add(TrustedClientIpMiddleware.SecretHeader, secret);
            }

            if (clientIp is not null)
            {
                request.Headers.Add(TrustedClientIpMiddleware.ClientIpHeader, clientIp);
            }

            using var response = await client.SendAsync(request);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    [Fact]
    public async Task With_the_secret_each_forwarded_visitor_gets_a_bucket_of_their_own()
    {
        // One peer (the web tier), fifteen different visitors: none is anywhere near their limit.
        using var webTier = factory.CreateClientFrom("198.51.100.10");

        var statuses = await SignInAttemptsAsync(
            webTier, attempt => (KoleksiApiFactory.InternalSecret, $"203.0.113.{attempt + 1}"));

        statuses.ShouldNotContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task With_the_secret_one_visitor_hammering_is_still_cut_off()
    {
        using var webTier = factory.CreateClientFrom("198.51.100.11");

        var statuses = await SignInAttemptsAsync(
            webTier, _ => (KoleksiApiFactory.InternalSecret, "203.0.113.200"));

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_visitors_budget_does_not_spend_anothers()
    {
        using var webTier = factory.CreateClientFrom("198.51.100.12");

        (await SignInAttemptsAsync(webTier, _ => (KoleksiApiFactory.InternalSecret, "203.0.113.201")))
            .ShouldContain(HttpStatusCode.TooManyRequests);

        var other = await SignInAttemptsAsync(
            webTier, attempt => attempt == 0 ? (KoleksiApiFactory.InternalSecret, "203.0.113.202") : (null, null));

        other[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task With_a_wrong_secret_the_client_address_header_is_ignored()
    {
        // Same peer, fifteen claimed visitors, but the secret is wrong: they are all the peer.
        using var attacker = factory.CreateClientFrom("198.51.100.13");

        var statuses = await SignInAttemptsAsync(
            attacker, attempt => ("not-the-secret-0123456789abcdef0123", $"203.0.113.{attempt + 1}"));

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Without_a_secret_the_client_address_header_is_ignored()
    {
        using var attacker = factory.CreateClientFrom("198.51.100.14");

        var statuses = await SignInAttemptsAsync(attacker, attempt => (null, $"203.0.113.{attempt + 1}"));

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_forged_address_cannot_be_used_to_spend_a_victims_budget()
    {
        // The victim's bucket is exhausted by the victim (via the web tier). An attacker naming the
        // victim's address without the secret neither borrows nor drains it: they stay in their own.
        using var webTier = factory.CreateClientFrom("198.51.100.15");
        using var attacker = factory.CreateClientFrom("198.51.100.16");

        var attackerStatuses = await SignInAttemptsAsync(attacker, _ => (null, "203.0.113.210"));

        attackerStatuses.ShouldContain(HttpStatusCode.TooManyRequests);

        var victim = await SignInAttemptsAsync(
            webTier, attempt => attempt == 0 ? (KoleksiApiFactory.InternalSecret, "203.0.113.210") : (null, null));

        victim[0].ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    // ─── The middleware on its own ───────────────────────────────────────────────

    private static async Task<IPAddress?> RemoteAddressAfterAsync(
        string secret, Action<IHeaderDictionary> headers, IPAddress? peer = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer ?? IPAddress.Parse("10.0.0.1");
        headers(context.Request.Headers);

        IPAddress? seen = null;
        var middleware = new TrustedClientIpMiddleware(
            next: ctx =>
            {
                seen = ctx.Connection.RemoteIpAddress;
                return Task.CompletedTask;
            },
            internalSecret: secret);

        await middleware.InvokeAsync(context);
        return seen;
    }

    private const string Secret = "unit-test-secret-0123456789abcdefghij";

    [Fact]
    public async Task A_matching_secret_replaces_the_peer_address_with_the_forwarded_one()
    {
        var seen = await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = Secret;
            h[TrustedClientIpMiddleware.ClientIpHeader] = "203.0.113.7";
        });

        seen.ShouldBe(IPAddress.Parse("203.0.113.7"));
    }

    [Fact]
    public async Task An_ipv6_address_and_an_ipv4_mapped_one_are_both_understood()
    {
        (await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = Secret;
            h[TrustedClientIpMiddleware.ClientIpHeader] = "2001:db8::1";
        })).ShouldBe(IPAddress.Parse("2001:db8::1"));

        (await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = Secret;
            h[TrustedClientIpMiddleware.ClientIpHeader] = "::ffff:203.0.113.9";
        })).ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Theory]
    [InlineData("wrong-secret-0123456789abcdefghijklmn")]
    [InlineData("")]
    [InlineData("unit-test-secret-0123456789abcdefghij ")]
    [InlineData("UNIT-TEST-SECRET-0123456789ABCDEFGHIJ")]
    public async Task A_wrong_secret_leaves_the_peer_address_alone(string presented)
    {
        var seen = await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = presented;
            h[TrustedClientIpMiddleware.ClientIpHeader] = "203.0.113.7";
        });

        seen.ShouldBe(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public async Task A_missing_secret_leaves_the_peer_address_alone()
    {
        var seen = await RemoteAddressAfterAsync(
            Secret, h => h[TrustedClientIpMiddleware.ClientIpHeader] = "203.0.113.7");

        seen.ShouldBe(IPAddress.Parse("10.0.0.1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.7, 203.0.113.8")]
    [InlineData("203.0.113.7:443")]
    [InlineData("999.1.1.1")]
    public async Task An_unparseable_forwarded_address_is_ignored_even_with_the_right_secret(string clientIp)
    {
        var seen = await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = Secret;
            h[TrustedClientIpMiddleware.ClientIpHeader] = clientIp;
        });

        seen.ShouldBe(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public async Task A_repeated_header_is_ambiguous_and_is_not_trusted()
    {
        var seen = await RemoteAddressAfterAsync(Secret, h =>
        {
            h[TrustedClientIpMiddleware.SecretHeader] = Secret;
            h[TrustedClientIpMiddleware.ClientIpHeader] = new Microsoft.Extensions.Primitives.StringValues(
                ["203.0.113.7", "203.0.113.8"]);
        });

        seen.ShouldBe(IPAddress.Parse("10.0.0.1"));
    }

    [Fact]
    public async Task The_address_header_alone_never_changes_anything()
    {
        var seen = await RemoteAddressAfterAsync(
            Secret, h => h[TrustedClientIpMiddleware.ClientIpHeader] = "203.0.113.7", peer: IPAddress.Parse("192.0.2.1"));

        seen.ShouldBe(IPAddress.Parse("192.0.2.1"));
    }
}
