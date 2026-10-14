using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>
/// The owner's warning rules: a broken commitment is a warning, three warnings ban the account, and every
/// clean month forgives one warning.
/// </summary>
public sealed class AccountStandingTests
{
    private const string User = "user-sub";

    [Fact]
    public void A_new_account_has_no_warnings_and_is_not_banned()
    {
        var standing = AccountStanding.Clean(User);

        standing.WarningsAt(TestClock.AtEpoch().UtcNow).ShouldBe(0);
        standing.IsBanned.ShouldBeFalse();
    }

    [Fact]
    public void A_broken_commitment_is_one_warning()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);

        standing.Record(ViolationType.AuctionWinNotPaid, "auction:A1", clock).ShouldBe(StandingOutcome.Warned);

        standing.WarningsAt(clock.UtcNow).ShouldBe(1);
        standing.IsBanned.ShouldBeFalse();
    }

    [Fact]
    public void The_third_warning_bans_the_account()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);

        standing.Record(ViolationType.AuctionWinNotPaid, "auction:A1", clock).ShouldBe(StandingOutcome.Warned);
        standing.Record(ViolationType.AcceptedOfferNotPaid, "offer:O1", clock).ShouldBe(StandingOutcome.Warned);
        standing.Record(ViolationType.AuctionWinNotPaid, "auction:A2", clock).ShouldBe(StandingOutcome.Banned);

        standing.IsBanned.ShouldBeTrue();
        standing.BannedAt.ShouldBe(clock.UtcNow);
        standing.WarningsAt(clock.UtcNow).ShouldBe(3);
    }

    [Fact]
    public void One_broken_deal_is_one_warning_however_many_ways_it_is_found()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);

        standing.Record(ViolationType.AuctionWinNotPaid, "auction:A1", clock).ShouldBe(StandingOutcome.Warned);
        standing.Record(ViolationType.AuctionWinNotPaid, "auction:A1", clock).ShouldBe(StandingOutcome.Duplicate);

        standing.WarningsAt(clock.UtcNow).ShouldBe(1);
        standing.Version.ShouldBe(1);
    }

    [Fact]
    public void A_banned_account_stays_banned_and_nothing_more_is_recorded()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);
        standing.Record(ViolationType.AuctionWinNotPaid, "a1", clock);
        standing.Record(ViolationType.AuctionWinNotPaid, "a2", clock);
        standing.Record(ViolationType.AuctionWinNotPaid, "a3", clock);

        standing.Record(ViolationType.AuctionWinNotPaid, "a4", clock).ShouldBe(StandingOutcome.AlreadyBanned);

        clock.Advance(TimeSpan.FromDays(400));
        standing.IsBanned.ShouldBeTrue();
        standing.WarningsAt(clock.UtcNow).ShouldBe(3);
    }

    [Fact]
    public void Thirty_days_with_no_new_warning_forgives_one()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);
        standing.Record(ViolationType.AuctionWinNotPaid, "a1", clock);
        standing.Record(ViolationType.AuctionWinNotPaid, "a2", clock);

        clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        standing.WarningsAt(clock.UtcNow).ShouldBe(2);

        clock.Advance(TimeSpan.FromSeconds(1));
        standing.WarningsAt(clock.UtcNow).ShouldBe(1);

        clock.Advance(TimeSpan.FromDays(30));
        standing.WarningsAt(clock.UtcNow).ShouldBe(0);

        clock.Advance(TimeSpan.FromDays(300));
        standing.WarningsAt(clock.UtcNow).ShouldBe(0);
    }

    [Fact]
    public void A_new_warning_counts_from_what_is_left_after_forgiveness_and_restarts_the_clock()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);
        standing.Record(ViolationType.AuctionWinNotPaid, "a1", clock);
        standing.Record(ViolationType.AuctionWinNotPaid, "a2", clock);

        // 31 days clean: two warnings have become one. A new one makes two, not three.
        clock.Advance(TimeSpan.FromDays(31));
        standing.Record(ViolationType.AcceptedOfferNotPaid, "o1", clock).ShouldBe(StandingOutcome.Warned);
        standing.WarningsAt(clock.UtcNow).ShouldBe(2);
        standing.IsBanned.ShouldBeFalse();

        // And the month starts again from this warning.
        clock.Advance(TimeSpan.FromDays(29));
        standing.WarningsAt(clock.UtcNow).ShouldBe(2);
        clock.Advance(TimeSpan.FromDays(1));
        standing.WarningsAt(clock.UtcNow).ShouldBe(1);
    }

    [Fact]
    public void Warnings_added_close_together_still_ban_without_waiting_for_a_clean_month()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);

        standing.Record(ViolationType.AuctionWinNotPaid, "a1", clock);
        clock.Advance(TimeSpan.FromDays(29));
        standing.Record(ViolationType.AuctionWinNotPaid, "a2", clock);
        clock.Advance(TimeSpan.FromDays(29));

        standing.Record(ViolationType.AuctionWinNotPaid, "a3", clock).ShouldBe(StandingOutcome.Banned);
    }

    [Fact]
    public void The_next_forgiveness_date_is_shown_while_there_is_something_to_forgive()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);
        standing.NextReductionAt(clock.UtcNow).ShouldBeNull();

        standing.Record(ViolationType.AuctionWinNotPaid, "a1", clock);
        var warnedAt = clock.UtcNow;
        standing.NextReductionAt(clock.UtcNow).ShouldBe(warnedAt + TimeSpan.FromDays(30));

        clock.Advance(TimeSpan.FromDays(31));
        standing.NextReductionAt(clock.UtcNow).ShouldBeNull();
    }

    [Fact]
    public void Only_the_most_recent_violations_are_kept()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);

        for (var i = 0; i < 30; i++)
        {
            standing.Record(ViolationType.AuctionWinNotPaid, $"a{i}", clock);
            clock.Advance(TimeSpan.FromDays(31));
        }

        standing.Recent.Count.ShouldBe(AccountStanding.MaxRememberedViolations);
        standing.Recent[^1].Reference.ShouldBe("a29");
    }

    [Fact]
    public void A_stored_record_comes_back_the_same()
    {
        var clock = TestClock.AtEpoch();
        var standing = AccountStanding.Clean(User);
        standing.Record(ViolationType.AcceptedOfferNotPaid, "offer:O1", clock);

        var read = AccountStanding.Rehydrate(
            standing.UserId, standing.StoredWarnings, standing.LastWarningAt, standing.BannedAt, standing.Recent, standing.Version);

        read.WarningsAt(clock.UtcNow).ShouldBe(1);
        read.Record(ViolationType.AcceptedOfferNotPaid, "offer:O1", clock).ShouldBe(StandingOutcome.Duplicate);
    }
}
