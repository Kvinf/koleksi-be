using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>The holiday list against a real table: each change lands with its audit rows or not at all, and a year is seeded once.</summary>
public sealed class HolidayStoreTests : DynamoTestBase
{
    private DynamoOptions Options => new() { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl };

    private DynamoHolidayStore Store => new(Table.Client, Options);

    private DynamoAuditLog Audit => new(Table.Client, Options, Table.Clock);

    private AuditEntry AuditFor(string action, string date, string? before = null, string? after = null)
    {
        var entry = AuditEntry.Create(Table.Clock, "staff-1", "alice", action, AuditTargets.Holiday, date, "req-" + Guid.NewGuid().ToString("N"), before: before, after: after);
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        return entry;
    }

    private async Task<IReadOnlyList<AuditEntry>> AuditOf(string date) =>
        (await Audit.ListAsync(new AuditQuery(null, AuditTargets.Holiday, date, null, 50), Ct)).Items;

    [DynamoLocalFact]
    public async Task Seeding_an_empty_year_writes_every_holiday_and_one_audit_entry_and_reading_gives_them_back_in_date_order()
    {
        var seeded = await Store.SeedAsync(2026, HolidaySeed.Year2026, AuditFor(AuditActions.HolidaySeeded, "2026"), Ct);

        seeded.ShouldBeTrue();
        var listed = await Store.ListAsync(2026, Ct);
        listed.ShouldBe([.. HolidaySeed.Year2026.OrderBy(h => h.Date, StringComparer.Ordinal)]);
        (await AuditOf("2026")).ShouldHaveSingleItem().Action.ShouldBe("holiday.seeded");
        (await Store.ListAsync(2027, Ct)).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task A_year_is_seeded_once_even_if_two_starts_race()
    {
        var attempts = await Task.WhenAll(
            Store.SeedAsync(2026, HolidaySeed.Year2026, AuditFor(AuditActions.HolidaySeeded, "2026"), Ct),
            Store.SeedAsync(2026, HolidaySeed.Year2026, AuditFor(AuditActions.HolidaySeeded, "2026"), Ct));

        attempts.Count(won => won).ShouldBe(1);
        (await Store.ListAsync(2026, Ct)).Count.ShouldBe(HolidaySeed.Year2026.Count);
        (await AuditOf("2026")).Count.ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task A_later_start_does_not_put_back_a_holiday_staff_removed()
    {
        await Store.SeedAsync(2026, HolidaySeed.Year2026, AuditFor(AuditActions.HolidaySeeded, "2026"), Ct);
        var removed = HolidaySeed.Year2026[3];
        (await Store.RemoveAsync(removed.Date, AuditFor(AuditActions.HolidayRemoved, removed.Date, before: removed.Name), Ct)).ShouldBe(HolidayRemoval.Removed);

        var seededAgain = await Store.SeedAsync(2026, HolidaySeed.Year2026, AuditFor(AuditActions.HolidaySeeded, "2026"), Ct);

        seededAgain.ShouldBeFalse();
        (await Store.FindAsync(removed.Date, Ct)).ShouldBeNull();
        (await Store.ListAsync(2026, Ct)).Count.ShouldBe(HolidaySeed.Year2026.Count - 1);
    }

    [DynamoLocalFact]
    public async Task Adding_a_holiday_writes_it_and_its_audit_rows_together()
    {
        var holiday = Holiday.Create("2026-12-31", "Cuti bersama contoh", HolidayKind.CollectiveLeave);

        (await Store.AddAsync(holiday, AuditFor(AuditActions.HolidayAdded, holiday.Date, after: holiday.Name), Ct)).ShouldBe(HolidayWrite.Written);

        (await Store.FindAsync("2026-12-31", Ct)).ShouldBe(holiday);
        (await AuditOf("2026-12-31")).ShouldHaveSingleItem().Action.ShouldBe("holiday.added");
    }

    [DynamoLocalFact]
    public async Task Adding_a_date_that_exists_is_refused_and_writes_no_audit_row()
    {
        var holiday = Holiday.Create("2026-12-31", "Satu", HolidayKind.National);
        await Store.AddAsync(holiday, AuditFor(AuditActions.HolidayAdded, holiday.Date), Ct);

        var second = await Store.AddAsync(
            Holiday.Create("2026-12-31", "Dua", HolidayKind.National), AuditFor(AuditActions.HolidayAdded, holiday.Date), Ct);

        second.ShouldBe(HolidayWrite.AlreadyExists);
        (await Store.FindAsync("2026-12-31", Ct))!.Name.ShouldBe("Satu");
        (await AuditOf("2026-12-31")).Count.ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Two_staff_adding_the_same_date_at_once_give_one_winner_and_one_audit_entry()
    {
        var results = await Task.WhenAll(
            Store.AddAsync(Holiday.Create("2026-12-30", "A", HolidayKind.National), AuditFor(AuditActions.HolidayAdded, "2026-12-30"), Ct),
            Store.AddAsync(Holiday.Create("2026-12-30", "B", HolidayKind.National), AuditFor(AuditActions.HolidayAdded, "2026-12-30"), Ct));

        results.Count(r => r == HolidayWrite.Written).ShouldBe(1);
        (await AuditOf("2026-12-30")).Count.ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task Removing_a_holiday_deletes_it_and_writes_its_audit_rows_together()
    {
        var holiday = Holiday.Create("2026-12-31", "Cuti bersama contoh", HolidayKind.CollectiveLeave);
        await Store.AddAsync(holiday, AuditFor(AuditActions.HolidayAdded, holiday.Date), Ct);

        (await Store.RemoveAsync(holiday.Date, AuditFor(AuditActions.HolidayRemoved, holiday.Date, before: holiday.Name), Ct)).ShouldBe(HolidayRemoval.Removed);

        (await Store.FindAsync(holiday.Date, Ct)).ShouldBeNull();
        (await AuditOf(holiday.Date)).Select(e => e.Action).ShouldBe(["holiday.removed", "holiday.added"]);
    }

    [DynamoLocalFact]
    public async Task Removing_a_date_that_is_not_listed_is_not_found_and_writes_no_audit_row()
    {
        (await Store.RemoveAsync("2026-12-31", AuditFor(AuditActions.HolidayRemoved, "2026-12-31"), Ct)).ShouldBe(HolidayRemoval.NotFound);
        (await Store.RemoveAsync("not-a-date", AuditFor(AuditActions.HolidayRemoved, "not-a-date"), Ct)).ShouldBe(HolidayRemoval.NotFound);

        (await AuditOf("2026-12-31")).ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task The_seed_marker_never_appears_in_a_years_list()
    {
        await Store.SeedAsync(2026, [Holiday.Create("2026-01-01", "Tahun Baru", HolidayKind.National)], AuditFor(AuditActions.HolidaySeeded, "2026"), Ct);

        var response = await Table.Client.QueryAsync(new QueryRequest
        {
            TableName = Table.TableName,
            KeyConditionExpression = "#pk = :pk",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#pk"] = DynamoTable.Pk },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":pk"] = new() { S = DynamoTable.HolidayPk(2026) } },
            ConsistentRead = true,
        });

        response.Count.ShouldBe(2, "one holiday and the marker");
        (await Store.ListAsync(2026, Ct)).ShouldHaveSingleItem().Date.ShouldBe("2026-01-01");
    }
}
