using System.Net;
using System.Net.Http.Json;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The abuse limits around verification codes, the ownership of uploaded media named in a
/// request, and the profile save rules. A class of its own so the clock it moves and the
/// per-user limiter it drains are not shared with the happy-path profile tests.
/// </summary>
public sealed class ProfileAbuseEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static string NewUserId() => Guid.NewGuid().ToString();

    private static long _phoneCounter = 81_200_000_000;

    private static string NewPhone() => "0" + Interlocked.Increment(ref _phoneCounter);

    private static Task<HttpResponseMessage> RequestCodeAsync(HttpClient client, string? phone) =>
        client.PostAsJsonAsync("/api/profile/phone/otp", new { phone });

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string? phone, string code) =>
        client.PostAsJsonAsync("/api/profile/phone/otp/verify", new { phone, code });

    // ─── Code requests: cooldown, hourly cap, per-user budget ────────────────────

    [Fact]
    public async Task A_second_code_to_the_same_number_inside_a_minute_is_refused_and_sends_nothing()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.cooldown");
        var phone = NewPhone();
        var sentBefore = factory.PhoneVerification.RequestCount;

        using var first = await RequestCodeAsync(client, phone);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await RequestCodeAsync(client, phone);
        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.otp_cooldown");

        factory.PhoneVerification.RequestCount.ShouldBe(sentBefore + 1);

        factory.Clock.Advance(VerificationLimits.Cooldown + TimeSpan.FromSeconds(1));

        using var later = await RequestCodeAsync(client, phone);
        later.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.PhoneVerification.RequestCount.ShouldBe(sentBefore + 2);
    }

    [Fact]
    public async Task The_cooldown_is_per_destination_so_another_number_is_not_held_up()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.twonumbers");

        (await RequestCodeAsync(client, NewPhone())).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RequestCodeAsync(client, NewPhone())).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_cooldown_belongs_to_the_account_that_asked_not_to_the_number()
    {
        var phone = NewPhone();
        using var one = factory.CreateClientAs(NewUserId(), "otp.userone");
        using var other = factory.CreateClientAs(NewUserId(), "otp.usertwo");

        (await RequestCodeAsync(one, phone)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RequestCodeAsync(other, phone)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_double_tap_carrying_one_idempotency_key_replays_instead_of_tripping_the_cooldown()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.doubletap");
        var phone = NewPhone();
        var key = Guid.NewGuid().ToString();
        var sentBefore = factory.PhoneVerification.RequestCount;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/profile/phone/otp")
            {
                Content = JsonContent.Create(new { phone }),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(request);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt}");
        }

        factory.PhoneVerification.RequestCount.ShouldBe(sentBefore + 1);
    }

    [Fact]
    public async Task Asking_for_codes_faster_than_the_per_user_budget_is_cut_off()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.flood");
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var response = await RequestCodeAsync(client, NewPhone());
            statuses.Add(response.StatusCode);
        }

        statuses.Take(3).ShouldAllBe(status => status == HttpStatusCode.OK);
        statuses.Skip(3).ShouldAllBe(status => status == HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task An_absurdly_long_phone_number_is_refused()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.longphone");

        using var response = await RequestCodeAsync(client, new string('1', 64));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.phone_invalid");
    }

    // ─── Guessing: a wrong code must not wipe a pending one ──────────────────────

    [Fact]
    public async Task A_strangers_wrong_guesses_cannot_wipe_a_victims_pending_code()
    {
        var phone = NewPhone();
        var victimId = NewUserId();
        using var victim = factory.CreateClientAs(victimId, "otp.victim");
        using var stranger = factory.CreateClientAs(NewUserId(), "otp.stranger");

        (await RequestCodeAsync(victim, phone)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = factory.PhoneVerification.SentCodes[phone];

        for (var guess = 0; guess < 8; guess++)
        {
            using var wrong = await VerifyAsync(stranger, phone, "000000");
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var verified = await VerifyAsync(victim, phone, code);

        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verified.ReadJsonAsync()).GetProperty("phoneVerified").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_wrong_code_is_counted_and_does_not_delete_the_pending_one()
    {
        var phone = NewPhone();
        using var client = factory.CreateClientAs(NewUserId(), "otp.typo");

        (await RequestCodeAsync(client, phone)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = factory.PhoneVerification.SentCodes[phone];

        (await VerifyAsync(client, phone, "000000")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var right = await VerifyAsync(client, phone, code);

        right.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task After_five_wrong_guesses_the_code_is_invalidated_even_for_the_right_answer()
    {
        var phone = NewPhone();
        using var client = factory.CreateClientAs(NewUserId(), "otp.guesser");

        (await RequestCodeAsync(client, phone)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = factory.PhoneVerification.SentCodes[phone];

        for (var guess = 0; guess < VerificationLimits.MaxAttempts; guess++)
        {
            (await VerifyAsync(client, phone, "000000")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var tooLate = await VerifyAsync(client, phone, code);
        tooLate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLate.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.code_invalid");
    }

    [Fact]
    public async Task The_email_channel_follows_the_same_guess_rules()
    {
        using var client = factory.CreateClientAs(NewUserId(), "otp.email");

        (await RequestCodeAsync(client, phone: null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = factory.EmailVerification.SentCodes[Fakes.FakeIdentityService.KnownEmail];

        (await VerifyAsync(client, phone: null, "000000")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var right = await VerifyAsync(client, phone: null, code);

        right.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Uploaded media named in a request ───────────────────────────────────────

    [Fact]
    public async Task An_avatar_must_be_the_callers_own_avatar_upload()
    {
        var userId = NewUserId();
        using var client = factory.CreateClientAs(userId, "media.avatar");

        var someoneElses = TestData.MediaKey(MediaPurpose.Avatar, NewUserId(), "theirs.jpg");
        var notAnAvatar = TestData.PhotoKey(userId, "photo");

        foreach (var bad in new[] { someoneElses, notAnAvatar, "avatars/../x/y.jpg", "avatars/never-uploaded.jpg" })
        {
            using var response = await client.PostAsJsonAsync("/api/profile/avatar", new { avatarKey = bad });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.avatar_not_uploaded", bad);
        }

        using var mine = await client.PostAsJsonAsync(
            "/api/profile/avatar", new { avatarKey = TestData.MediaKey(MediaPurpose.Avatar, userId, "me.jpg") });
        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task While_ID_verification_is_off_no_identity_document_is_accepted_or_granted()
    {
        var features = factory.Services.GetRequiredService<FeaturesOptions>();
        var userId = NewUserId();
        using var client = factory.CreateClientAs(userId, "media.noid");
        var document = TestData.MediaKey(MediaPurpose.IdentityDocument, userId, "ktp.jpg");

        features.IdVerificationEnabled = false;
        try
        {
            using var grant = await client.PostAsJsonAsync(
                "/api/media/upload-grant", new { purpose = "IdentityDocument", contentType = "image/jpeg", contentLength = 100_000 });
            grant.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await grant.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("feature.unavailable");

            using var submit = await client.PostAsJsonAsync("/api/profile/id-verification", new { idPhotoKey = document });
            submit.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await submit.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("feature.unavailable");

            // The other purposes are unaffected.
            using var avatar = await client.PostAsJsonAsync(
                "/api/media/upload-grant", new { purpose = "Avatar", contentType = "image/jpeg", contentLength = 100_000 });
            avatar.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            features.IdVerificationEnabled = true;
        }
    }

    [Fact]
    public async Task Identity_verification_keeps_the_callers_own_document_key_and_never_returns_it()
    {
        var userId = NewUserId();
        using var client = factory.CreateClientAs(userId, "media.identity");
        var document = TestData.MediaKey(MediaPurpose.IdentityDocument, userId, "ktp.jpg");

        using var response = await client.PostAsJsonAsync("/api/profile/id-verification", new { idPhotoKey = document });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("idVerificationStatus").GetString().ShouldBe("Pending");

        var wire = await response.ReadTextAsync();
        wire.ShouldNotContain("identity/");
        wire.ShouldNotContain("ktp");

        var stored = await factory.Profiles.FindAsync(userId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.IdDocumentKey.ShouldBe(document);

        using var read = await client.GetAsync("/api/profile");
        (await read.ReadTextAsync()).ShouldNotContain("ktp");
    }

    [Fact]
    public async Task Identity_verification_refuses_someone_elses_document_or_a_public_photo()
    {
        var userId = NewUserId();
        using var client = factory.CreateClientAs(userId, "media.identitybad");

        foreach (var bad in new[]
        {
            TestData.MediaKey(MediaPurpose.IdentityDocument, NewUserId(), "theirs.jpg"),
            TestData.MediaKey(MediaPurpose.Avatar, userId, "me.jpg"),
            TestData.PhotoKey(userId, "photo"),
        })
        {
            using var response = await client.PostAsJsonAsync("/api/profile/id-verification", new { idPhotoKey = bad });
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, bad);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.id_photos_not_uploaded", bad);
        }

        var stored = await factory.Profiles.FindAsync(userId, CancellationToken.None);
        (stored?.IdDocumentKey).ShouldBeNull();
    }

    [Fact]
    public async Task A_listing_photo_must_be_the_sellers_own_upload()
    {
        var sellerId = NewUserId();
        using var client = factory.CreateClientAs(sellerId, "media.listing");

        object Body(string key) => new
        {
            categoryId = TestData.CardsCategoryId,
            title = "PSA 10 Charizard Base Set Holo 1999",
            priceRupiah = 2_500_000,
            format = "FixedPrice",
            condition = "PSA 10",
            city = "Bandung",
            description = "Kondisi mulus.",
            photos = new[] { new { storageKey = key, width = 1200, height = 1200 } },
            publish = false,
        };

        foreach (var bad in new[]
        {
            TestData.PhotoKey(NewUserId(), "theirs"),
            TestData.MediaKey(MediaPurpose.Avatar, sellerId, "me.jpg"),
            TestData.PhotoKey(sellerId, "photo") + "/original",
            "listings/test-photo.jpg",
        })
        {
            using var response = await client.PostAsJsonAsync("/api/listings", Body(bad));
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, bad);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_not_uploaded", bad);
        }

        using var created = await client.PostAsJsonAsync("/api/listings", Body(TestData.PhotoKey(sellerId, "photo")));
        created.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Editing a draft is held to the same rule.
        var id = (await created.ReadJsonAsync()).GetProperty("listingId").GetString()!;
        using var swapped = await client.PutAsJsonAsync(
            $"/api/listings/{id}", Body(TestData.PhotoKey(NewUserId(), "theirs")));
        swapped.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await swapped.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("listing.photo_not_uploaded");
    }

    // ─── Saving a profile ────────────────────────────────────────────────────────

    private static object AddressRow(string label, bool isDefault) => new
    {
        label,
        recipientName = "Budi",
        phone = "081234567890",
        line = "Jl. Mawar No. 1",
        city = "Bandung",
        province = "Jawa Barat",
        postalCode = "40123",
        isDefault,
    };

    [Fact]
    public async Task Saving_name_addresses_and_bank_account_does_not_need_a_verified_phone()
    {
        using var client = factory.CreateClientAs(NewUserId(), "save.unverified");

        using var save = await client.PutAsJsonAsync("/api/profile", new
        {
            fullName = "Budi Kolektor",
            addresses = new[] { AddressRow("Rumah", true) },
        });
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await save.ReadJsonAsync()).GetProperty("phoneVerified").GetBoolean().ShouldBeFalse();

        using var bank = await client.PostAsJsonAsync(
            "/api/profile/bank-account", new { bankName = "BCA", accountNumber = "1234567890", accountHolderName = "Budi" });
        bank.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var reload = await client.GetAsync("/api/profile");
        var body = await reload.ReadJsonAsync();
        body.GetProperty("fullName").GetString().ShouldBe("Budi Kolektor");
        body.GetProperty("addresses").GetArrayLength().ShouldBe(1);
        body.GetProperty("bankAccount").GetProperty("bankName").GetString().ShouldBe("BCA");
    }

    [Fact]
    public async Task Two_addresses_flagged_default_save_with_the_last_one_winning()
    {
        using var client = factory.CreateClientAs(NewUserId(), "save.twodefaults");

        using var save = await client.PutAsJsonAsync("/api/profile", new
        {
            fullName = "Budi Kolektor",
            addresses = new[] { AddressRow("Rumah", true), AddressRow("Kantor", true), AddressRow("Kos", false) },
        });

        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        var addresses = (await save.ReadJsonAsync()).GetProperty("addresses").EnumerateArray().ToList();
        addresses.Select(a => a.GetProperty("label").GetString()).ShouldBe(["Rumah", "Kantor", "Kos"]);
        addresses.Select(a => a.GetProperty("isDefault").GetBoolean()).ShouldBe([false, true, false]);
    }
}
