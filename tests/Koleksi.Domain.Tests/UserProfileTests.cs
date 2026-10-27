using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class UserProfileTests
{
    private static UserProfile Fresh(TestClock clock) => UserProfile.CreateEmpty("user-sub", clock);

    private static Address AddressRow(string label, bool isDefault) => Address.Create(
        id: null, label, "Dwi Rahmawati", "081234567890", "Jl. Tebet Raya No. 5", "Jakarta Selatan", "DKI Jakarta", "12810", isDefault);

    [Fact]
    public void When_two_addresses_are_flagged_default_the_last_one_wins_and_nothing_throws()
    {
        var clock = TestClock.AtEpoch();
        var profile = Fresh(clock);

        profile.UpdateDetails(
            "Dwi",
            [AddressRow("Rumah", isDefault: true), AddressRow("Kantor", isDefault: true), AddressRow("Kos", isDefault: false)],
            clock);

        profile.Addresses.Select(a => a.Label).ShouldBe(["Rumah", "Kantor", "Kos"]);
        profile.Addresses.Select(a => a.IsDefault).ShouldBe([false, true, false]);
    }

    [Fact]
    public void Three_defaults_leave_exactly_the_last()
    {
        var clock = TestClock.AtEpoch();
        var profile = Fresh(clock);

        profile.UpdateDetails(
            "Dwi",
            [AddressRow("A", true), AddressRow("B", true), AddressRow("C", true)],
            clock);

        profile.Addresses.Count(a => a.IsDefault).ShouldBe(1);
        profile.Addresses[2].IsDefault.ShouldBeTrue();
    }

    [Fact]
    public void A_single_default_and_a_list_with_no_default_are_left_as_given()
    {
        var clock = TestClock.AtEpoch();
        var profile = Fresh(clock);

        profile.UpdateDetails("Dwi", [AddressRow("A", false), AddressRow("B", true)], clock);
        profile.Addresses.Select(a => a.IsDefault).ShouldBe([false, true]);

        profile.UpdateDetails("Dwi", [AddressRow("A", false), AddressRow("B", false)], clock);
        profile.Addresses.Any(a => a.IsDefault).ShouldBeFalse();
    }

    [Fact]
    public void Saving_name_and_addresses_does_not_depend_on_the_phone_being_verified()
    {
        // Verifying a phone is its own step; nothing about saving details may wait on it.
        var clock = TestClock.AtEpoch();
        var profile = Fresh(clock);
        profile.PhoneVerified.ShouldBeFalse();

        Should.NotThrow(() => profile.UpdateDetails("Dwi", [AddressRow("Rumah", true)], clock));
        Should.NotThrow(() => profile.SetBankAccount(BankAccount.Create("BCA", "1234567890", "Dwi Rahmawati"), clock));

        profile.FullName.ShouldBe("Dwi");
        profile.PhoneVerified.ShouldBeFalse();
    }

    [Fact]
    public void More_than_the_address_limit_is_still_refused()
    {
        var clock = TestClock.AtEpoch();
        var profile = Fresh(clock);
        var tooMany = Enumerable.Range(0, UserProfile.MaxAddresses + 1).Select(i => AddressRow($"A{i}", false)).ToList();

        var failure = Should.Throw<Koleksi.Domain.Common.DomainException>(() => profile.UpdateDetails("Dwi", tooMany, clock));
        failure.Code.ShouldBe("profile.too_many_addresses");
    }
}
