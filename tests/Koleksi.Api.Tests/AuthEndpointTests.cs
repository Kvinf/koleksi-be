using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The only anonymous write surface in the product.
/// </summary>
/// <remarks>
/// <para>
/// Two properties matter more than anything else on these routes, and both are the kind
/// that decay quietly: every unsuccessful sign-in must be indistinguishable from every
/// other, or the endpoint becomes an account-enumeration oracle; and the rate limit must
/// actually bite, or a credential dump can be worked through at HTTP speed.
/// </para>
/// <para>
/// Every test presents its own caller address. That is not bookkeeping — the sign-in
/// bucket holds ten attempts per address, so without distinct addresses these tests would
/// spend each other's budget, and the first one to run would be the only one that passed.
/// Addresses come from <c>203.0.113.0/24</c>, the block RFC 5737 reserves for
/// documentation, so none of them is a real host.
/// </para>
/// </remarks>
public sealed class AuthEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task Correct_credentials_return_a_session()
    {
        using var client = factory.CreateClientFrom("203.0.113.1");

        using var response = await SignInAsync(client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.ReadJsonAsync();
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("username").GetString().ShouldBe(FakeIdentityService.KnownUsername);

        // The refresh token is returned to the web tier, which puts it in an HttpOnly
        // cookie. It must be here, and it must never appear in a page's HTML.
        body.GetProperty("refreshToken").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("203.0.113.2", "budi@example.test", "wrong-password")]
    [InlineData("203.0.113.3", "nobody@example.test", "correct-horse-battery-staple")]
    [InlineData("203.0.113.4", "nobody@example.test", "also-wrong")]
    [InlineData("203.0.113.5", "", "")]
    public async Task Every_unsuccessful_sign_in_is_answered_identically(
        string clientAddress, string email, string password)
    {
        // A wrong password and an account that does not exist must be the same answer.
        // Anything finer turns sign-in into a way to test whether an address is registered
        // — which is worth more to an attacker than the distinction is worth to a user.
        using var client = factory.CreateClientFrom(clientAddress);

        using var response = await SignInAsync(client, email, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.invalid_credentials");
    }

    [Fact]
    public async Task A_failed_sign_in_says_nothing_about_the_account()
    {
        using var client = factory.CreateClientFrom("203.0.113.6");

        using var response = await SignInAsync(client, "nobody@example.test", "wrong");

        var text = (await response.ReadTextAsync()).ToUpperInvariant();
        text.ShouldNotContain("NOT FOUND");
        text.ShouldNotContain("NOT CONFIRMED");
        text.ShouldNotContain("PASSWORD");
        text.ShouldNotContain("NOBODY@EXAMPLE.TEST");
    }

    [Fact]
    public async Task Registering_does_not_hand_back_a_session()
    {
        // Sign-in is a separate, separately rate-limited step. If registration returned
        // tokens, a registration flood would be a session-minting machine.
        using var client = factory.CreateClientFrom("203.0.113.7");

        using var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            username = "kolektor.baru",
            email = "baru@example.test",
            password = "correct-horse-battery-staple",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var text = (await response.ReadTextAsync()).ToUpperInvariant();
        text.ShouldNotContain("ACCESSTOKEN");
        text.ShouldNotContain("REFRESHTOKEN");
    }

    [Fact]
    public async Task A_taken_username_is_reported_because_a_handle_is_already_public()
    {
        // Unlike an email address, a handle is the shop name — browsing to /toko/{handle}
        // reveals the same thing. Saying so is not a leak, and not saying so makes the
        // form unusable.
        factory.Identity.NextRegistration = RegistrationOutcome.Failed("auth.username_taken");

        try
        {
            using var client = factory.CreateClientFrom("203.0.113.8");

            using var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "budi.collectibles",
                email = "another@example.test",
                password = "correct-horse-battery-staple",
            });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.username_taken");
        }
        finally
        {
            factory.Identity.NextRegistration = RegistrationOutcome.AwaitingConfirmation;
        }
    }

    [Fact]
    public async Task A_taken_email_is_reported_without_blaming_the_username()
    {
        // The username cleared its own uniqueness check — attributing the failure to
        // auth.username_taken here would tell whoever's signing up the wrong thing is
        // wrong, and would also confirm the email is already registered.
        factory.Identity.NextRegistration = RegistrationOutcome.Failed("auth.registration_failed");

        try
        {
            using var client = factory.CreateClientFrom("203.0.113.8");

            using var response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                username = "budi.collectibles",
                email = "already-registered@example.test",
                password = "correct-horse-battery-staple",
            });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.registration_failed");
        }
        finally
        {
            factory.Identity.NextRegistration = RegistrationOutcome.AwaitingConfirmation;
        }
    }

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token_rather_than_forgetting_it()
    {
        // Clearing a cookie leaves the token usable for its full thirty days by anyone who
        // captured a copy. Signing out has to mean something on the server too.
        using var client = factory.CreateClientFrom("203.0.113.9");

        using var response = await client.PostAsJsonAsync("/api/auth/logout", new
        {
            refreshToken = "valid-refresh",
            username = FakeIdentityService.KnownUsername,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Identity.Revoked.ShouldContain("valid-refresh");
    }

    [Fact]
    public async Task A_stale_refresh_token_is_refused()
    {
        using var client = factory.CreateClientFrom("203.0.113.10");

        using var response = await client.PostAsJsonAsync("/api/auth/refresh", new
        {
            refreshToken = "revoked-long-ago",
            username = FakeIdentityService.KnownUsername,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Guessing_repeatedly_is_cut_off()
    {
        // The bucket holds ten. Twenty wrong passwords in a row must not all be tried,
        // because that rate is what makes a credential dump worth working through.
        using var attacker = factory.CreateClientFrom("203.0.113.200");

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var response = await SignInAsync(attacker, "nobody@example.test", $"guess-{attempt}");
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task One_address_being_throttled_does_not_lock_anyone_else_out()
    {
        // The partition is the point. Without it, one determined attacker locks every
        // legitimate user out of signing in — a denial of service delivered by our own
        // rate limiter. It also proves UseForwardedHeaders is doing its job: without it
        // every caller would share the gateway's address and share one bucket.
        using var attacker = factory.CreateClientFrom("203.0.113.201");

        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var burst = await SignInAsync(attacker, "nobody@example.test", $"guess-{attempt}");
            _ = burst.StatusCode;
        }

        using var bystander = factory.CreateClientFrom("203.0.113.202");
        using var response = await SignInAsync(
            bystander, FakeIdentityService.KnownEmail, FakeIdentityService.KnownPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_matching_otp_code_for_an_unconfirmed_account_names_it_instead_of_signing_in()
    {
        // Reaching this branch already required the session and code the identity service
        // issued, so — unlike every other OTP failure — naming the account here is not an
        // enumeration leak: see OtpVerifyOutcome.UnconfirmedUsername.
        factory.Identity.NextOtpUnconfirmedUsername = "budi.collectibles";

        try
        {
            using var client = factory.CreateClientFrom("203.0.113.12");

            using var response = await client.PostAsJsonAsync(
                "/api/auth/otp/verify",
                new
                {
                    email = FakeIdentityService.KnownEmail,
                    session = FakeIdentityService.KnownOtpSession,
                    code = FakeIdentityService.KnownOtpCode,
                });

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            var body = await response.ReadJsonAsync();
            body.GetProperty("code").GetString().ShouldBe("auth.otp_account_not_confirmed");
            body.GetProperty("username").GetString().ShouldBe("budi.collectibles");
        }
        finally
        {
            factory.Identity.NextOtpUnconfirmedUsername = null;
        }
    }

    [Fact]
    public async Task The_auth_routes_carry_the_security_headers()
    {
        using var client = factory.CreateClientFrom("203.0.113.11");

        using var response = await SignInAsync(client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownPassword);

        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");

        // A session response must never be held by a shared cache.
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    private static Task<HttpResponseMessage> SignInAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });
}
