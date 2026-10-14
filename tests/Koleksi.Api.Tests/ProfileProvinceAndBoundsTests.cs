using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The address book's province (QA UI-06) and the bounds on profile input (QA D-07).
/// </summary>
public sealed class ProfileProvinceAndBoundsTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static string NewUserId() => Guid.NewGuid().ToString();

    private static Dictionary<string, object?> Row(
        string? id = null,
        string label = "Rumah",
        string recipientName = "Budi",
        string phone = "081234567890",
        string line = "Jl. Mawar No. 1",
        string city = "Bandung",
        string? province = "Jawa Barat",
        string postalCode = "40123") => new()
    {
        ["id"] = id,
        ["label"] = label,
        ["recipientName"] = recipientName,
        ["phone"] = phone,
        ["line"] = line,
        ["city"] = city,
        ["province"] = province,
        ["postalCode"] = postalCode,
        ["isDefault"] = true,
    };

    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, params object[] addresses) =>
        await client.PutAsJsonAsync("/api/profile", new { fullName = "Budi Kolektor", addresses });

    // --- UI-06: province ------------------------------------------------------------------------

    [Fact]
    public async Task A_saved_address_returns_its_province()
    {
        using var client = factory.CreateClientAs(NewUserId(), "province.roundtrip");

        using var save = await SaveAsync(client, Row(province: "  Jawa Timur "));

        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await save.ReadJsonAsync()).GetProperty("addresses")[0].GetProperty("province").GetString().ShouldBe("Jawa Timur");

        using var reload = await client.GetAsync("/api/profile");
        (await reload.ReadJsonAsync()).GetProperty("addresses")[0].GetProperty("province").GetString().ShouldBe("Jawa Timur");
    }

    [Theory]
    [InlineData(null, "profile.province_required")]
    [InlineData("", "profile.province_required")]
    [InlineData("   ", "profile.province_required")]
    [InlineData("J", "profile.province_invalid")]
    public async Task A_new_address_without_a_usable_province_is_refused(string? province, string code)
    {
        using var client = factory.CreateClientAs(NewUserId(), "province.missing");

        using var response = await SaveAsync(client, Row(province: province));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_province_is_capped_at_eighty_characters()
    {
        using var client = factory.CreateClientAs(NewUserId(), "province.long");

        using var tooLong = await SaveAsync(client, Row(province: new string('p', 81)));
        tooLong.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooLong.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.province_invalid");

        using var atTheLimit = await SaveAsync(client, Row(province: new string('p', 80)));
        atTheLimit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_address_stored_before_provinces_existed_loads_and_can_be_saved_again_but_a_new_one_cannot_skip_it()
    {
        var userId = NewUserId();
        var legacy = new Address("legacy-address-id", "Rumah", "Budi", "081234567890", "Jl. Lama 1", "Bandung", null, "40123", true);
        var profile = UserProfile.CreateEmpty(userId, new MutableClock());
        profile.UpdateDetails("Budi Lama", [legacy], new MutableClock());
        await factory.Profiles.AddAsync(profile, CancellationToken.None);
        using var client = factory.CreateClientAs(userId, "province.legacy");

        // Loads: the province is simply absent.
        using var read = await client.GetAsync("/api/profile");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await read.ReadJsonAsync()).GetProperty("addresses")[0];
        stored.GetProperty("id").GetString().ShouldBe("legacy-address-id");
        if (stored.TryGetProperty("province", out var missing))
        {
            missing.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        }

        // Saved again untouched (its own id, still no province): not forced to fix it to rename.
        using var resave = await SaveAsync(client, Row(id: "legacy-address-id", province: null, line: "Jl. Lama 1"));
        resave.StatusCode.ShouldBe(HttpStatusCode.OK);

        // But a brand-new row, or a different id, still needs one.
        using var added = await SaveAsync(
            client, Row(id: "legacy-address-id", province: null, line: "Jl. Lama 1"), Row(label: "Kantor", province: null));
        added.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await added.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.province_required");

        // And once the owner does give it a province, it is kept.
        using var fixedUp = await SaveAsync(client, Row(id: "legacy-address-id", province: "Jawa Barat", line: "Jl. Lama 1"));
        fixedUp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await fixedUp.ReadJsonAsync()).GetProperty("addresses")[0].GetProperty("province").GetString().ShouldBe("Jawa Barat");
    }

    // --- D-07: bounds ---------------------------------------------------------------------------

    [Fact]
    public async Task A_save_without_an_address_list_is_a_bad_request_not_a_server_error()
    {
        using var client = factory.CreateClientAs(NewUserId(), "bounds.nulladdresses");

        using var omitted = await client.PutAsJsonAsync("/api/profile", new { fullName = "Budi" });
        omitted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await omitted.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.addresses_invalid");

        using var explicitNull = await client.PutAsJsonAsync("/api/profile", new { fullName = "Budi", addresses = (object?)null });
        explicitNull.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var nullRow = await client.PutAsJsonAsync("/api/profile", new { fullName = "Budi", addresses = new object?[] { null } });
        nullRow.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("label", 41, "profile.address_label_too_long")]
    [InlineData("recipientName", 101, "profile.address_name_too_long")]
    [InlineData("phone", 21, "profile.address_phone_too_long")]
    [InlineData("line", 241, "profile.address_line_too_long")]
    [InlineData("city", 81, "profile.address_city_too_long")]
    public async Task An_oversized_address_field_is_refused_and_the_limit_itself_is_fine(string field, int tooLong, string code)
    {
        using var client = factory.CreateClientAs(NewUserId(), "bounds." + field);

        var bad = Row();
        bad[field] = new string('x', tooLong);
        using var refused = await SaveAsync(client, bad);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);

        var ok = Row();
        ok[field] = new string('x', tooLong - 1);
        using var accepted = await SaveAsync(client, ok);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_oversized_full_name_is_refused()
    {
        using var client = factory.CreateClientAs(NewUserId(), "bounds.fullname");

        using var response = await client.PutAsJsonAsync(
            "/api/profile", new { fullName = new string('n', UserProfile.MaxFullNameLength + 1), addresses = Array.Empty<object>() });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.name_too_long");
    }

    [Fact]
    public async Task An_oversized_bank_name_or_holder_is_refused()
    {
        using var client = factory.CreateClientAs(NewUserId(), "bounds.bank");

        using var bank = await client.PostAsJsonAsync(
            "/api/profile/bank-account",
            new { bankName = new string('b', BankAccount.MaxBankNameLength + 1), accountNumber = "1234567890", accountHolderName = "Budi" });
        bank.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await bank.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.bank_account_invalid");

        using var holder = await client.PostAsJsonAsync(
            "/api/profile/bank-account",
            new { bankName = "BCA", accountNumber = "1234567890", accountHolderName = new string('h', BankAccount.MaxAccountHolderNameLength + 1) });
        holder.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        using var fine = await client.PostAsJsonAsync(
            "/api/profile/bank-account", new { bankName = "BCA", accountNumber = "1234567890", accountHolderName = "Budi" });
        fine.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
