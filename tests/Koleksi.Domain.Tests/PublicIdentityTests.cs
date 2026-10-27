using Koleksi.Domain.Common;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class PublicIdentityTests
{
    [Theory]
    [InlineData("budi", "budi")]
    [InlineData("  Budi_Collectibles", "budi_collectibles")]
    [InlineData("kartu-kolektor99", "kartu-kolektor99")]
    [InlineData("a1b", "a1b")]
    public void A_well_formed_handle_is_accepted_and_lower_cased(string raw, string expected)
    {
        PublicHandle.TryNormalize(raw, out var handle).ShouldBeNull();
        handle.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a")]
    [InlineData("-budi")]
    [InlineData("budi_")]
    [InlineData("bu di")]
    [InlineData("budi.k")]
    [InlineData("kevin@gmail.com")]
    [InlineData("budi/../admin")]
    [InlineData("büdi")]
    [InlineData("budi-kolektor-yang-sangat-panjang-sekali")]
    public void A_handle_that_is_not_url_safe_or_is_the_wrong_length_is_refused(string? raw) =>
        PublicHandle.TryNormalize(raw, out _).ShouldBe("handle.invalid_format");

    [Theory]
    [InlineData("admin")]
    [InlineData("TOKO")]
    [InlineData("support")]
    [InlineData("koleksi")]
    [InlineData("koleksi-official")]
    [InlineData("koleksiid")]
    public void A_reserved_handle_or_one_that_poses_as_the_platform_is_refused(string raw) =>
        PublicHandle.TryNormalize(raw, out _).ShouldBe("handle.reserved");

    // Look-alike spellings (QA P-05): digits and symbols standing in for letters, and separators dropped.
    [Theory]
    [InlineData("k0leksi")]
    [InlineData("k0leks1")]
    [InlineData("ko1eksi")]
    [InlineData("ko-leksi")]
    [InlineData("k_o_l_e_k_s_i")]
    [InlineData("4dmin")]
    [InlineData("4dm1n")]
    [InlineData("adm1n")]
    [InlineData("ad-min")]
    [InlineData("5upport")]
    [InlineData("st4ff")]
    [InlineData("t0k0")]
    [InlineData("0fficial")]
    public void A_look_alike_of_a_reserved_handle_is_refused_too(string raw) =>
        PublicHandle.TryNormalize(raw, out _).ShouldBe("handle.reserved");

    [Theory]
    [InlineData("k0leksi fan")]
    [InlineData("K0leksi Official")]
    [InlineData("4dmin Toko")]
    [InlineData("Toko 5upport")]
    [InlineData("St4ff Koleksi")]
    public void A_look_alike_display_name_that_poses_as_the_platform_is_refused(string raw) =>
        PublicDisplayName.TryNormalize(raw, out _).ShouldBe("profile.display_name_invalid");

    [Theory]
    [InlineData("budi99")]
    [InlineData("kartu-kolektor99")]
    [InlineData("a1b")]
    [InlineData("tokoku")]
    [InlineData("gunpla-4-life")]
    public void Ordinary_handles_with_digits_and_hyphens_are_still_accepted(string raw)
    {
        // "tokoku" and the rest contain a reserved word only as part of something longer, which has always been allowed.
        PublicHandle.TryNormalize(raw, out _).ShouldBeNull();
    }

    [Fact]
    public void A_generated_handle_is_itself_a_valid_canonical_handle_and_is_not_constant()
    {
        var handles = Enumerable.Range(0, 50).Select(_ => PublicHandle.Generate()).ToList();

        handles.ShouldAllBe(handle => PublicHandle.IsCanonical(handle));
        handles.Distinct().Count().ShouldBeGreaterThan(45);
    }

    [Theory]
    [InlineData("budi", true)]
    [InlineData("Budi", false)]
    [InlineData(" budi", false)]
    [InlineData("ab", false)]
    public void Only_an_already_canonical_spelling_counts_as_canonical(string raw, bool expected) =>
        PublicHandle.IsCanonical(raw).ShouldBe(expected);

    [Theory]
    [InlineData("Toko  Budi\tKolektor", "Toko Budi Kolektor")]
    [InlineData("  Slab Vault  ", "Slab Vault")]
    [InlineData("Budi​", "Budi")]
    public void A_display_name_has_its_whitespace_and_invisible_characters_cleaned(string raw, string expected)
    {
        PublicDisplayName.TryNormalize(raw, out var name).ShouldBeNull();
        name.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_display_name_means_show_the_handle(string? raw)
    {
        PublicDisplayName.TryNormalize(raw, out var name).ShouldBeNull();
        name.ShouldBeNull();
    }

    [Theory]
    [InlineData("kevin@gmail.com")]
    [InlineData("x")]
    public void A_display_name_that_looks_like_an_email_or_is_too_short_is_refused(string raw) =>
        PublicDisplayName.TryNormalize(raw, out _).ShouldBe("profile.display_name_invalid");

    [Fact]
    public void A_display_name_over_the_limit_is_refused() =>
        PublicDisplayName.TryNormalize(new string('a', PublicDisplayName.MaxLength + 1), out _)
            .ShouldBe("profile.display_name_invalid");

    private static PublicProfile NewProfile(TestClock clock) =>
        PublicProfile.Create("user-1", "user-abcd2345", clock.UtcNow, clock);

    [Fact]
    public void The_shown_name_is_the_display_name_when_set_and_the_handle_otherwise()
    {
        var clock = TestClock.AtEpoch();
        var profile = NewProfile(clock);
        profile.ShownName.ShouldBe("user-abcd2345");

        profile.Edit("user-abcd2345", "Toko Budi", clock);

        profile.ShownName.ShouldBe("Toko Budi");
        profile.Handle.ShouldBe("user-abcd2345");
    }

    [Fact]
    public void The_first_handle_change_is_free_and_the_next_waits_out_the_cooldown()
    {
        var clock = TestClock.AtEpoch();
        var profile = NewProfile(clock);

        profile.Edit("budi", null, clock);
        profile.Handle.ShouldBe("budi");
        profile.HandleChangedAt.ShouldBe(clock.UtcNow);

        clock.Advance(TimeSpan.FromDays(10));
        Should.Throw<DomainException>(() => profile.Edit("budi2", null, clock)).Code.ShouldBe("handle.change_too_soon");
        profile.Handle.ShouldBe("budi");

        clock.Advance(PublicProfile.HandleChangeCooldown);
        Should.NotThrow(() => profile.Edit("budi2", null, clock));
        profile.Handle.ShouldBe("budi2");
    }

    [Fact]
    public void Saving_the_same_handle_is_not_a_change_and_does_not_start_the_cooldown()
    {
        var clock = TestClock.AtEpoch();
        var profile = NewProfile(clock);

        profile.Edit("user-abcd2345", "Toko Budi", clock);

        profile.HandleChangedAt.ShouldBeNull();
        profile.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("admin", "handle.reserved")]
    [InlineData("a b", "handle.invalid_format")]
    public void An_edit_to_an_unacceptable_handle_changes_nothing(string handle, string code)
    {
        var clock = TestClock.AtEpoch();
        var profile = NewProfile(clock);

        Should.Throw<DomainException>(() => profile.Edit(handle, "Nama", clock)).Code.ShouldBe(code);

        profile.Handle.ShouldBe("user-abcd2345");
        profile.DisplayName.ShouldBeNull();
        profile.Version.ShouldBe(0);
    }
}
