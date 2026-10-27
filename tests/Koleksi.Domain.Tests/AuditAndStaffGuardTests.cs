using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>The audit entry's validation, and the rule that a staff member cannot act on themselves.</summary>
public sealed class AuditAndStaffGuardTests
{
    private const string Actor = "11111111-1111-4111-8111-111111111111";

    private static AuditEntry Entry(
        string action = "staff.session_started",
        string targetType = "staff",
        string targetId = Actor,
        string? reason = null,
        string? before = null,
        string? after = null,
        string actorName = "alice") =>
        AuditEntry.Create(TestClock.AtEpoch(), Actor, actorName, action, targetType, targetId, "req-1", reason, before, after);

    [Fact]
    public void An_entry_takes_its_time_from_the_clock_and_a_time_ordered_id()
    {
        var clock = TestClock.AtEpoch();

        var first = AuditEntry.Create(clock, Actor, "alice", "staff.session_started", "staff", Actor, "req-1");
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = AuditEntry.Create(clock, Actor, "alice", "staff.session_started", "staff", Actor, "req-2");

        first.At.ShouldBe(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        EntityId.IsValid(first.Id).ShouldBeTrue();
        string.CompareOrdinal(second.Id, first.Id).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Blank_optional_text_is_stored_as_absent()
    {
        var entry = Entry(reason: "   ", before: "", after: null);

        entry.Reason.ShouldBeNull();
        entry.Before.ShouldBeNull();
        entry.After.ShouldBeNull();
    }

    [Fact]
    public void Text_is_trimmed()
    {
        Entry(reason: "  spam listing  ").Reason.ShouldBe("spam listing");
    }

    [Theory]
    [InlineData("Staff.Session")]
    [InlineData("staff session")]
    [InlineData("st")]
    [InlineData("1staff")]
    [InlineData("")]
    public void An_action_must_be_lower_case_words(string action)
    {
        var failure = Should.Throw<DomainException>(() => Entry(action: action));

        failure.Code.ShouldBe("admin.audit_invalid");
    }

    [Fact]
    public void An_action_of_exactly_sixty_four_characters_is_accepted_and_sixty_five_is_not()
    {
        Should.NotThrow(() => Entry(action: "a" + new string('b', 63)));
        Should.Throw<DomainException>(() => Entry(action: "a" + new string('b', 64))).Code.ShouldBe("admin.audit_invalid");
    }

    [Fact]
    public void A_reason_may_be_a_thousand_characters_and_no_more()
    {
        Should.NotThrow(() => Entry(reason: new string('r', 1000)));
        Should.Throw<DomainException>(() => Entry(reason: new string('r', 1001))).Code.ShouldBe("admin.audit_invalid");
    }

    [Fact]
    public void The_before_and_after_summaries_are_bounded_too()
    {
        Should.Throw<DomainException>(() => Entry(before: new string('b', 1001)));
        Should.Throw<DomainException>(() => Entry(after: new string('a', 1001)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has\nnewline")]
    public void Ids_and_names_must_be_present_and_free_of_control_characters(string bad)
    {
        Should.Throw<DomainException>(() => Entry(targetId: bad));
        Should.Throw<DomainException>(() => Entry(actorName: bad));
    }

    [Fact]
    public void An_id_longer_than_the_limit_is_refused()
    {
        Should.Throw<DomainException>(() => Entry(targetId: new string('x', 129)));
        Should.NotThrow(() => Entry(targetId: new string('x', 128)));
    }

    [Fact]
    public void A_staff_member_cannot_act_on_their_own_account()
    {
        var failure = Should.Throw<DomainException>(() => StaffGuard.EnsureNotSelf(Actor, Actor));

        failure.Code.ShouldBe("admin.cannot_act_on_self");
    }

    [Fact]
    public void A_staff_member_can_act_on_someone_else()
    {
        Should.NotThrow(() => StaffGuard.EnsureNotSelf(Actor, "22222222-2222-4222-8222-222222222222"));
    }

    [Fact]
    public void The_comparison_is_exact_not_case_folded()
    {
        // Ids are opaque. Treating two different strings as one person would block a legitimate action.
        Should.NotThrow(() => StaffGuard.EnsureNotSelf("abc", "ABC"));
    }

    [Fact]
    public void A_staff_member_cannot_act_on_another_staff_account()
    {
        IReadOnlySet<string> staff = new HashSet<string>(StringComparer.Ordinal) { "bob", "carol" };

        Should.Throw<DomainException>(() => StaffGuard.EnsureNotStaff(staff, "bob")).Code.ShouldBe("admin.cannot_act_on_staff");
        Should.NotThrow(() => StaffGuard.EnsureNotStaff(staff, "dave"));
    }
}
