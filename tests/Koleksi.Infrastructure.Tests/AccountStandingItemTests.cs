using Koleksi.Domain.Common;
using Koleksi.Domain.Users;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// An account's standing survives being written to and read back from its DynamoDB item. This is the
/// mapping the in-memory fake skips, and a mistake in it only shows once a real row exists.
/// </summary>
public sealed class AccountStandingItemTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    [Fact]
    public void A_clean_account_round_trips()
    {
        var item = DynamoAccountStandingRepository.ToItem(AccountStanding.Clean("user-1"));

        var back = DynamoAccountStandingRepository.ToDomain(item);

        back.UserId.ShouldBe("user-1");
        back.StoredWarnings.ShouldBe(0);
        back.IsBanned.ShouldBeFalse();
        back.Recent.ShouldBeEmpty();
    }

    [Fact]
    public void Warnings_violations_and_the_ban_round_trip()
    {
        var clock = new FixedClock(Start);
        var standing = AccountStanding.Clean("user-2");
        standing.Record(ViolationType.AcceptedOfferNotPaid, "offer:a", clock);
        standing.Record(ViolationType.AuctionWinNotPaid, "auction:b", clock);
        standing.Record(ViolationType.AcceptedOfferNotPaid, "offer:c", clock);
        standing.IsBanned.ShouldBeTrue();

        var back = DynamoAccountStandingRepository.ToDomain(DynamoAccountStandingRepository.ToItem(standing));

        back.UserId.ShouldBe("user-2");
        back.StoredWarnings.ShouldBe(3);
        back.IsBanned.ShouldBeTrue();
        back.BannedAt.ShouldBe(standing.BannedAt);
        back.LastWarningAt.ShouldBe(standing.LastWarningAt);
        back.Recent.Select(v => (v.Type, v.Reference)).ShouldBe(
            [
                (ViolationType.AcceptedOfferNotPaid, "offer:a"),
                (ViolationType.AuctionWinNotPaid, "auction:b"),
                (ViolationType.AcceptedOfferNotPaid, "offer:c"),
            ]);
        back.Version.ShouldBe(standing.Version);
    }

    [Theory]
    [InlineData("USER#abc", "abc")]
    [InlineData("USER#a#b", "a#b")]
    public void The_account_id_is_read_back_out_of_the_partition_key(string pk, string expected)
    {
        DynamoTable.UserIdFromPk(pk).ShouldBe(expected);
        DynamoTable.UserIdFromPk(DynamoTable.UserPk(expected)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("USER#")]
    [InlineData("ORDER#abc")]
    [InlineData("")]
    public void Anything_that_is_not_a_user_key_is_refused(string pk)
    {
        Should.Throw<ArgumentException>(() => DynamoTable.UserIdFromPk(pk));
    }
}
