using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The signed-in account's own profile: identity, addresses, phone verification, the
/// payout bank account, and the generic media upload grant it and identity verification
/// both depend on.
/// </summary>
public sealed class ProfileEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string UserId = "01JPROFILEUSER0000000000001";
    private const string Username = "kolektor.uji";

    [Fact]
    public async Task A_fresh_account_reads_back_an_empty_profile_rather_than_404()
    {
        using var client = factory.CreateClientAs("01JPROFILEUSER0000000000EMPTY", Username);

        using var response = await client.GetAsync("/api/profile");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("fullName").GetString().ShouldBe(string.Empty);
        body.GetProperty("phoneVerified").GetBoolean().ShouldBeFalse();
        body.GetProperty("addresses").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Saving_a_profile_persists_the_name_and_addresses()
    {
        using var client = factory.CreateClientAs(UserId, Username);

        using var save = await client.PutAsJsonAsync("/api/profile", new
        {
            fullName = "Budi Kolektor",
            addresses = new[]
            {
                new
                {
                    label = "Rumah",
                    recipientName = "Budi",
                    phone = "081234567890",
                    line = "Jl. Mawar No. 1",
                    city = "Bandung",
                    province = "Jawa Barat",
                    postalCode = "40123",
                    isDefault = true,
                },
            },
        });

        save.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var reload = await client.GetAsync("/api/profile");
        var body = await reload.ReadJsonAsync();
        body.GetProperty("fullName").GetString().ShouldBe("Budi Kolektor");

        var addresses = body.GetProperty("addresses");
        addresses.GetArrayLength().ShouldBe(1);
        addresses[0].GetProperty("city").GetString().ShouldBe("Bandung");
        addresses[0].GetProperty("province").GetString().ShouldBe("Jawa Barat");
        addresses[0].GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        using var client = factory.CreateClientAs("01JPROFILEUSER0000000000BLNK", Username);

        using var response = await client.PutAsJsonAsync(
            "/api/profile", new { fullName = "", addresses = Array.Empty<object>() });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_repeated_idempotency_key_replays_the_first_save_rather_than_writing_twice()
    {
        const string userId = "01JPROFILEUSER0000000000IDEM";
        using var client = factory.CreateClientAs(userId, Username);
        var key = Guid.NewGuid().ToString();

        using var first = new HttpRequestMessage(HttpMethod.Put, "/api/profile")
        {
            Content = JsonContent.Create(new { fullName = "Nama Pertama", addresses = Array.Empty<object>() }),
        };
        first.Headers.Add("Idempotency-Key", key);
        using var firstResponse = await client.SendAsync(first);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Same key, a different body — the point of the test is that the second call never
        // runs at all, so whatever it claims to change must not have taken effect.
        using var second = new HttpRequestMessage(HttpMethod.Put, "/api/profile")
        {
            Content = JsonContent.Create(new { fullName = "Nama Kedua", addresses = Array.Empty<object>() }),
        };
        second.Headers.Add("Idempotency-Key", key);
        using var secondResponse = await client.SendAsync(second);

        secondResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await secondResponse.ReadJsonAsync()).GetProperty("fullName").GetString().ShouldBe("Nama Pertama");
    }

    [Fact]
    public async Task Phone_verification_marks_the_number_verified_only_after_the_right_code()
    {
        const string userId = "01JPROFILEUSER0000000000PHNE";
        using var client = factory.CreateClientAs(userId, Username);
        const string phone = "081200000001";

        using var wrong = await client.PostAsJsonAsync(
            "/api/profile/phone/otp/verify", new { phone, code = "000000" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var requested = await client.PostAsJsonAsync("/api/profile/phone/otp", new { phone });
        requested.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = factory.PhoneVerification.SentCodes[phone];

        using var verified = await client.PostAsJsonAsync(
            "/api/profile/phone/otp/verify", new { phone, code });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await verified.ReadJsonAsync();
        body.GetProperty("phoneVerified").GetBoolean().ShouldBeTrue();
        body.GetProperty("phone").GetString().ShouldBe(phone);
    }

    [Fact]
    public async Task An_empty_phone_falls_back_to_the_accounts_own_email()
    {
        const string userId = "01JPROFILEUSER0000000000MAIL";
        using var client = factory.CreateClientAs(userId, Username);

        using var requested = await client.PostAsJsonAsync("/api/profile/phone/otp", new { phone = "" });
        requested.StatusCode.ShouldBe(HttpStatusCode.OK);

        var requestBody = await requested.ReadJsonAsync();
        requestBody.GetProperty("channel").GetString().ShouldBe("email");
        requestBody.GetProperty("email").GetString().ShouldBe(FakeIdentityService.KnownEmail);

        var code = factory.EmailVerification.SentCodes[FakeIdentityService.KnownEmail];

        using var verified = await client.PostAsJsonAsync(
            "/api/profile/phone/otp/verify", new { phone = "", code });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await verified.ReadJsonAsync();
        body.GetProperty("phoneVerified").GetBoolean().ShouldBeTrue();

        // Verified via email, not SMS — there is no phone number to record. A null Phone
        // is omitted from the JSON entirely (WhenWritingNull), not sent as an explicit null.
        body.TryGetProperty("phone", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_same_code_cannot_be_used_twice()
    {
        const string userId = "01JPROFILEUSER0000000000REPL";
        using var client = factory.CreateClientAs(userId, Username);
        const string phone = "081200000002";

        await client.PostAsJsonAsync("/api/profile/phone/otp", new { phone });
        var code = factory.PhoneVerification.SentCodes[phone];

        using var first = await client.PostAsJsonAsync("/api/profile/phone/otp/verify", new { phone, code });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var replay = await client.PostAsJsonAsync("/api/profile/phone/otp/verify", new { phone, code });
        replay.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setting_an_avatar_requires_the_key_to_have_actually_been_uploaded()
    {
        const string userId = "01JPROFILEUSER0000000000AVTR";
        using var client = factory.CreateClientAs(userId, Username);
        factory.Media.NextVerifyUploadedResult = false;

        try
        {
            using var response = await client.PostAsJsonAsync(
                "/api/profile/avatar", new { avatarKey = "avatars/never-uploaded.jpg" });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString()
                .ShouldBe("profile.avatar_not_uploaded");
        }
        finally
        {
            factory.Media.NextVerifyUploadedResult = true;
        }
    }

    [Fact]
    public async Task Anonymous_callers_cannot_read_or_write_a_profile()
    {
        using var client = factory.CreateClient();

        using var get = await client.GetAsync("/api/profile");
        get.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var put = await client.PutAsJsonAsync(
            "/api/profile", new { fullName = "Nobody", addresses = Array.Empty<object>() });
        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_bank_account_is_stored_masked_in_every_response()
    {
        const string userId = "01JPROFILEUSER0000000000BANK";
        using var client = factory.CreateClientAs(userId, Username);

        using var response = await client.PostAsJsonAsync("/api/profile/bank-account", new
        {
            bankName = "BCA",
            accountNumber = "1234567890",
            accountHolderName = "Budi Kolektor",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.ReadTextAsync();
        text.ShouldNotContain("1234567890");

        var body = await response.ReadJsonAsync();
        body.GetProperty("bankAccount").GetProperty("maskedAccountNumber").GetString().ShouldEndWith("7890");
    }

    [Fact]
    public async Task The_upload_grant_endpoint_mints_a_key_under_the_callers_own_id()
    {
        using var client = factory.CreateClientAs(UserId, Username);

        using var response = await client.PostAsJsonAsync("/api/media/upload-grant", new
        {
            purpose = "Avatar",
            contentType = "image/jpeg",
            contentLength = 12345,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        (body.GetProperty("storageKey").GetString() ?? string.Empty).ShouldContain(UserId);
    }
}
