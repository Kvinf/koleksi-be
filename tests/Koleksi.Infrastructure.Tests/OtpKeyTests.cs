using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Key composition for verification challenges and their throttle. A challenge is keyed by the
/// account that asked as well as the destination: that is what stops a stranger's guesses from
/// reaching somebody else's pending code.
/// </summary>
public sealed class OtpKeyTests
{
    private const string Owner = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    private const string Other = "9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";

    [Fact]
    public void A_phone_challenge_is_keyed_by_account_and_number()
    {
        DynamoTable.PhoneOtpPkValue(Owner, "+6281234567890").ShouldBe($"PHONEOTP#{Owner}#+6281234567890");
        DynamoTable.PhoneOtpPkValue(Owner, "+6281234567890")
            .ShouldNotBe(DynamoTable.PhoneOtpPkValue(Other, "+6281234567890"));
    }

    [Fact]
    public void An_email_challenge_is_keyed_by_account_and_address()
    {
        DynamoTable.EmailOtpPkValue(Owner, "budi@example.test").ShouldBe($"EMAILOTP#{Owner}#budi@example.test");
        DynamoTable.EmailOtpPkValue(Owner, "budi@example.test")
            .ShouldNotBe(DynamoTable.EmailOtpPkValue(Other, "budi@example.test"));
    }

    [Fact]
    public void Throttle_rows_share_one_partition_per_account()
    {
        DynamoTable.OtpThrottlePk(Owner).ShouldBe($"OTPTHROTTLE#{Owner}");
        DynamoTable.OtpCooldownSk("ab12").ShouldBe("#COOLDOWN#ab12");
        DynamoTable.OtpHourSk(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero)).ShouldBe("#HOUR#2026030109");
    }

    [Theory]
    [InlineData("", "+62812")]
    [InlineData(Owner, "")]
    [InlineData("   ", "+62812")]
    public void An_empty_component_is_refused(string owner, string destination)
    {
        Should.Throw<ArgumentException>(() => DynamoTable.PhoneOtpPkValue(owner, destination));
        Should.Throw<ArgumentException>(() => DynamoTable.EmailOtpPkValue(owner, destination));
    }
}
