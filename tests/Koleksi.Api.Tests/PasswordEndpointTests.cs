using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Forgotten-password reset and changing your own password.
/// </summary>
/// <remarks>
/// <para>
/// The reset pair is anonymous, so the property that decays quietly here is the same one the
/// sign-in tests guard: nothing the endpoint says may depend on whether an account exists.
/// That is checked on the wire, against the response bytes, not against a typed model.
/// </para>
/// <para>
/// Addresses come from <c>198.51.100.0/24</c> (RFC 5737 documentation space) so the
/// per-address bucket one test spends is never the one another test is counting on.
/// </para>
/// </remarks>
public sealed class PasswordEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string StrongPassword = "Sandi-Baru-2026!";

    [Fact]
    public async Task Forgot_gives_the_same_answer_for_a_registered_and_an_unknown_address()
    {
        // The whole contract: a stranger probing addresses must not be able to tell which are
        // registered, so status and body have to be byte-identical.
        using var client = factory.CreateClientFrom("198.51.100.1");

        using var known = await ForgotAsync(client, FakeIdentityService.KnownEmail);
        using var unknown = await ForgotAsync(client, "nobody@example.test");

        known.StatusCode.ShouldBe(HttpStatusCode.OK);
        unknown.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await known.ReadTextAsync()).ShouldBe(await unknown.ReadTextAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Forgot_with_a_blank_address_is_the_same_neutral_answer(string email)
    {
        using var client = factory.CreateClientFrom("198.51.100.2");

        using var baseline = await ForgotAsync(client, "nobody@example.test");
        using var response = await ForgotAsync(client, email);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadTextAsync()).ShouldBe(await baseline.ReadTextAsync());
    }

    [Fact]
    public async Task Forgot_says_nothing_about_the_account()
    {
        using var client = factory.CreateClientFrom("198.51.100.3");

        using var response = await ForgotAsync(client, "nobody@example.test");

        var text = (await response.ReadTextAsync()).ToUpperInvariant();
        text.ShouldNotContain("NOT FOUND");
        text.ShouldNotContain("NOT CONFIRMED");
        text.ShouldNotContain("NOBODY@EXAMPLE.TEST");
        text.ShouldNotContain("GOOGLE");
    }

    [Fact]
    public async Task Forgot_hands_a_dev_code_back_only_when_the_identity_service_issued_one()
    {
        // The local stub is the only thing that ever issues one (and it is compiled out of
        // Release), and only for an address that has an account — the OTP flow's rule exactly.
        factory.Identity.NextResetDevCode = "123456";

        try
        {
            using var client = factory.CreateClientFrom("198.51.100.4");

            using var known = await ForgotAsync(client, FakeIdentityService.KnownEmail);
            using var unknown = await ForgotAsync(client, "nobody@example.test");

            (await known.ReadJsonAsync()).GetProperty("devCode").GetString().ShouldBe("123456");

            // Null properties are omitted from the wire, so "absent" is how "no code" reads.
            (await unknown.ReadJsonAsync()).TryGetProperty("devCode", out var none).ShouldBeFalse(
                "an unknown address must not carry a dev code");
            none.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
        }
        finally
        {
            factory.Identity.NextResetDevCode = null;
        }
    }

    [Fact]
    public async Task Reset_with_the_right_code_sets_the_new_password()
    {
        factory.Identity.PasswordsSet.Clear();
        using var client = factory.CreateClientFrom("198.51.100.5");

        using var response = await ResetAsync(
            client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownResetCode, StrongPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Identity.PasswordsSet.ShouldContain(StrongPassword);
    }

    [Theory]
    [InlineData("198.51.100.6", "budi@example.test", "000000")]
    [InlineData("198.51.100.7", "nobody@example.test", "654321")]
    [InlineData("198.51.100.8", "nobody@example.test", "000000")]
    [InlineData("198.51.100.9", "", "654321")]
    [InlineData("198.51.100.10", "budi@example.test", "")]
    public async Task Reset_never_separates_a_wrong_code_from_an_unknown_account(
        string clientAddress, string email, string code)
    {
        using var client = factory.CreateClientFrom(clientAddress);

        using var response = await ResetAsync(client, email, code, StrongPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.code_invalid");
    }

    [Fact]
    public async Task Reset_reports_an_expired_code_with_its_own_code()
    {
        factory.Identity.NextPasswordFailure = "auth.code_expired";

        try
        {
            using var client = factory.CreateClientFrom("198.51.100.11");

            using var response = await ResetAsync(
                client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownResetCode, StrongPassword);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.code_expired");
        }
        finally
        {
            factory.Identity.NextPasswordFailure = null;
        }
    }

    [Theory]
    [InlineData("short1!")]
    [InlineData("")]
    public async Task Reset_refuses_a_password_too_short_for_the_policy_before_asking_the_provider(string password)
    {
        factory.Identity.PasswordsSet.Clear();
        using var client = factory.CreateClientFrom("198.51.100.12");

        using var response = await ResetAsync(
            client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownResetCode, password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.password_too_weak");
        factory.Identity.PasswordsSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reset_passes_the_providers_weak_password_verdict_through()
    {
        // Cognito's own policy (including the breached-password check no browser can run)
        // decides this; the endpoint's job is only to carry the stable code.
        factory.Identity.NextPasswordFailure = "auth.password_too_weak";

        try
        {
            using var client = factory.CreateClientFrom("198.51.100.13");

            using var response = await ResetAsync(
                client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownResetCode, "Password1!");

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.password_too_weak");
        }
        finally
        {
            factory.Identity.NextPasswordFailure = null;
        }
    }

    [Fact]
    public async Task A_reset_response_never_contains_a_password()
    {
        using var client = factory.CreateClientFrom("198.51.100.14");

        using var response = await ResetAsync(
            client, FakeIdentityService.KnownEmail, FakeIdentityService.KnownResetCode, StrongPassword);

        (await response.ReadTextAsync()).ShouldNotContain(StrongPassword);
    }

    [Fact]
    public async Task Forgot_and_reset_spend_the_same_per_address_bucket_as_sign_in()
    {
        // One allowance per caller address across every anonymous auth route, or the reset
        // form would be a way round the sign-in limit. Ten tokens to start: twenty requests
        // split across forgot and reset must overrun it, and sign-in from the same address
        // must then be refused as well.
        using var attacker = factory.CreateClientFrom("198.51.100.200");

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var response = attempt % 2 == 0
                ? await ForgotAsync(attacker, $"guess-{attempt}@example.test")
                : await ResetAsync(attacker, "nobody@example.test", "000000", StrongPassword);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);

        using var signIn = await attacker.PostAsJsonAsync(
            "/api/auth/login",
            new { email = FakeIdentityService.KnownEmail, password = FakeIdentityService.KnownPassword });
        signIn.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var bystander = factory.CreateClientFrom("198.51.100.201");
        using var fine = await ForgotAsync(bystander, FakeIdentityService.KnownEmail);
        fine.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_a_password_requires_signing_in()
    {
        using var client = factory.CreateClientFrom("198.51.100.20");

        using var response = await client.PostAsJsonAsync(
            "/api/auth/password/change",
            new { currentPassword = FakeIdentityService.KnownCurrentPassword, newPassword = StrongPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_a_password_with_the_wrong_current_one_is_refused()
    {
        factory.Identity.PasswordsSet.Clear();
        using var client = factory.CreateClientAs("01JBPASSWORDAAAAAAAAAAAAAA", "ganti.sandi.satu");

        using var response = await ChangeAsync(client, "not-my-password", StrongPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.current_password_invalid");
        factory.Identity.PasswordsSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Changing_a_password_with_the_right_current_one_succeeds()
    {
        factory.Identity.PasswordsSet.Clear();
        using var client = factory.CreateClientAs("01JBPASSWORDBBBBBBBBBBBBBB", "ganti.sandi.dua");

        using var response = await ChangeAsync(client, FakeIdentityService.KnownCurrentPassword, StrongPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        factory.Identity.PasswordsSet.ShouldContain(StrongPassword);
    }

    [Fact]
    public async Task Changing_a_password_rejects_a_new_one_too_short_for_the_policy()
    {
        using var client = factory.CreateClientAs("01JBPASSWORDCCCCCCCCCCCCCC", "ganti.sandi.tiga");

        using var response = await ChangeAsync(client, FakeIdentityService.KnownCurrentPassword, "Ab1!");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.password_too_weak");
    }

    [Fact]
    public async Task A_google_only_account_is_told_it_has_no_password_to_change()
    {
        factory.Identity.NextPasswordFailure = "auth.password_unsupported";
        factory.Identity.NextCanChangePassword = false;

        try
        {
            using var client = factory.CreateClientAs("01JBPASSWORDDDDDDDDDDDDDDD", "akun.google");

            using var status = await client.GetAsync("/api/auth/password/status");
            status.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await status.ReadJsonAsync()).GetProperty("canChange").GetBoolean().ShouldBeFalse();

            using var response = await ChangeAsync(client, "anything", StrongPassword);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("auth.password_unsupported");
        }
        finally
        {
            factory.Identity.NextPasswordFailure = null;
            factory.Identity.NextCanChangePassword = true;
        }
    }

    [Fact]
    public async Task The_password_status_requires_signing_in_and_reports_a_normal_account_as_changeable()
    {
        using var anonymous = factory.CreateClientFrom("198.51.100.21");
        using var refused = await anonymous.GetAsync("/api/auth/password/status");
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = factory.CreateClientAs("01JBPASSWORDEEEEEEEEEEEEEE", "ganti.sandi.lima");
        using var response = await client.GetAsync("/api/auth/password/status");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("canChange").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Guessing_the_current_password_repeatedly_is_cut_off_per_account()
    {
        // The bucket holds five. Fifteen wrong guesses must not all be tried, and one
        // account's exhaustion must not spend another account's budget.
        using var attacker = factory.CreateClientAs("01JBPASSWORDFFFFFFFFFFFFFF", "penebak.sandi");

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 15; attempt++)
        {
            using var response = await ChangeAsync(attacker, $"guess-{attempt}", StrongPassword);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);

        using var bystander = factory.CreateClientAs("01JBPASSWORDGGGGGGGGGGGGGG", "pengguna.lain");
        using var fine = await ChangeAsync(bystander, FakeIdentityService.KnownCurrentPassword, StrongPassword);
        fine.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static Task<HttpResponseMessage> ForgotAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/password/forgot", new { email });

    private static Task<HttpResponseMessage> ResetAsync(
        HttpClient client, string email, string code, string newPassword) =>
        client.PostAsJsonAsync("/api/auth/password/reset", new { email, code, newPassword });

    private static Task<HttpResponseMessage> ChangeAsync(
        HttpClient client, string currentPassword, string newPassword) =>
        client.PostAsJsonAsync("/api/auth/password/change", new { currentPassword, newPassword });
}
