using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>How an audit entry is laid out in the table, and how its pages are asked for, without a database.</summary>
public sealed class AdminAuditStorageTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 3, 4, 5, TimeSpan.Zero);

    private static AuditEntry Entry(string action = "staff.session_started") =>
        AuditEntry.Create(
            new FixedClock(At), "staff-1", "alice", action, "staff", "staff-1", "req-1",
            reason: "because", before: "none", after: "one");

    [Fact]
    public void An_entry_is_three_puts_in_the_audit_partitions_of_one_transaction()
    {
        var items = AuditItems.For(Entry(), "koleksi-test");

        items.Count.ShouldBe(AuditItems.ItemsPerEntry);
        items.ShouldAllBe(item => item.Put != null && item.Update == null && item.Delete == null);

        var partitions = items.Select(item => item.Put.Item[DynamoTable.Pk].S).ToList();
        partitions.ShouldBe(["AUDIT#ALL#202610", "AUDIT#ACTOR#staff-1", "AUDIT#TARGET#staff#staff-1"]);
        partitions.ShouldAllBe(pk => pk.StartsWith(DynamoTable.AuditPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_put_refuses_to_replace_an_existing_row()
    {
        foreach (var item in AuditItems.For(Entry(), "koleksi-test"))
        {
            item.Put.ConditionExpression.ShouldBe("attribute_not_exists(pk)");
            item.Put.TableName.ShouldBe("koleksi-test");
        }
    }

    [Fact]
    public void The_three_copies_share_a_sort_key_and_carry_the_same_content()
    {
        var items = AuditItems.For(Entry(), "koleksi-test").Select(item => item.Put.Item).ToList();

        items.Select(item => item[DynamoTable.Sk].S).Distinct().Count().ShouldBe(1);
        foreach (var name in new[] { "id", "at", "actorId", "actorName", "action", "targetType", "targetId", "requestId", "reason", "before", "after" })
        {
            items.Select(item => item[name].S).Distinct().Count().ShouldBe(1, name);
        }
    }

    [Fact]
    public void Absent_text_is_left_out_not_stored_as_empty()
    {
        var bare = AuditEntry.Create(new FixedClock(At), "staff-1", "alice", "staff.session_started", "staff", "staff-1", "req-1");

        var item = AuditItems.For(bare, "koleksi-test")[0].Put.Item;

        item.ContainsKey("reason").ShouldBeFalse();
        item.ContainsKey("before").ShouldBeFalse();
        item.ContainsKey("after").ShouldBeFalse();
    }

    [Fact]
    public void A_stored_row_reads_back_as_the_same_entry()
    {
        var entry = Entry();
        var item = AuditItems.For(entry, "koleksi-test")[0].Put.Item;

        var restored = AuditItems.ToEntry(item);

        restored.ShouldBe(entry);
    }

    [Fact]
    public void Newer_entries_sort_after_older_ones_so_a_descending_query_is_newest_first()
    {
        var older = DynamoTable.AuditSk(At, EntityId.New(At));
        var newer = DynamoTable.AuditSk(At.AddSeconds(1), EntityId.New(At.AddSeconds(1)));

        string.CompareOrdinal(newer, older).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Two_months_use_two_partitions()
    {
        DynamoTable.AuditMonthPk(new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero)).ShouldBe("AUDIT#ALL#202610");
        DynamoTable.AuditMonthPk(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)).ShouldBe("AUDIT#ALL#202611");
    }

    [Fact]
    public void The_month_is_the_utc_month_whatever_the_offset_of_the_instant()
    {
        // 06:30 on 1 November in Jakarta is still 31 October in UTC.
        DynamoTable.AuditMonthPk(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.FromHours(7))).ShouldBe("AUDIT#ALL#202610");
    }

    // ─── Cursors: caller input, parsed strictly ─────────────────────────────────

    [Fact]
    public void A_sort_key_cursor_round_trips()
    {
        var sk = DynamoTable.AuditSk(At, EntityId.New(At));

        AuditCursor.ParseSortKey(AuditCursor.ForSortKey(sk)).ShouldBe(sk);
    }

    [Fact]
    public void A_month_cursor_round_trips()
    {
        var sk = DynamoTable.AuditSk(At, EntityId.New(At));

        var (month, parsedSk) = AuditCursor.ParseMonthCursor(AuditCursor.ForMonth(At, sk));

        month.ShouldBe(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        parsedSk.ShouldBe(sk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("YWJj")]
    [InlineData("MjAyNjEwfEZPTw")]
    public void A_cursor_that_is_not_ours_is_refused_with_a_coded_error(string cursor)
    {
        Should.Throw<DomainException>(() => AuditCursor.ParseSortKey(cursor)).Code.ShouldBe("admin.audit_query_invalid");
        Should.Throw<DomainException>(() => AuditCursor.ParseMonthCursor(cursor)).Code.ShouldBe("admin.audit_query_invalid");
    }

    [Fact]
    public void A_cursor_cannot_smuggle_another_partition()
    {
        // A cursor is a sort key and nothing else. One that names a different partition is not a valid shape.
        var sneaky = AuditCursor.ForSortKey("AUDIT#ACTOR#someone-else");

        Should.Throw<DomainException>(() => AuditCursor.ParseSortKey(sneaky));
    }

    // ─── What reaches DynamoDB ──────────────────────────────────────────────────

    [Fact]
    public async Task Appending_sends_one_transaction_carrying_all_three_rows_and_a_retry_token()
    {
        var recorder = DynamoRecorder.Create(out var dynamo);
        var log = new DynamoAuditLog(dynamo, new DynamoOptions { TableName = "koleksi-test" }, new FixedClock(At));
        var entry = Entry();

        await log.AppendAsync(entry, CancellationToken.None);

        var request = recorder.Transactions.ShouldHaveSingleItem();
        request.TransactItems.Count.ShouldBe(3);
        request.ClientRequestToken.ShouldBe(entry.Id);
        recorder.Writes.ShouldBe(1, "an entry is never written outside a transaction");
    }

    [Fact]
    public async Task Listing_every_entry_reads_one_partition_without_scanning()
    {
        var recorder = DynamoRecorder.Create(out var dynamo);
        var log = new DynamoAuditLog(dynamo, new DynamoOptions { TableName = "koleksi-test" }, new FixedClock(At));

        await log.ListAsync(new AuditQuery(null, null, null, null, 25), CancellationToken.None);

        recorder.Scans.ShouldBe(0);
        var query = recorder.Queries.First();
        query.ExpressionAttributeValues[":pk"].S.ShouldBe("AUDIT#ALL#202610");
        query.ScanIndexForward.ShouldBe(false);
        query.Limit.ShouldBe(25);
    }

    [Fact]
    public async Task An_empty_month_makes_the_listing_look_one_month_back_up_to_the_limit_and_no_further()
    {
        var recorder = DynamoRecorder.Create(out var dynamo);
        var log = new DynamoAuditLog(dynamo, new DynamoOptions { TableName = "koleksi-test" }, new FixedClock(At));

        var page = await log.ListAsync(new AuditQuery(null, null, null, null, 10), CancellationToken.None);

        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
        recorder.Queries.Count.ShouldBe(DynamoAuditLog.MaxMonthsBack + 1);
        recorder.Queries[0].ExpressionAttributeValues[":pk"].S.ShouldBe("AUDIT#ALL#202610");
        recorder.Queries[1].ExpressionAttributeValues[":pk"].S.ShouldBe("AUDIT#ALL#202609");
    }

    [Fact]
    public async Task Filtering_by_actor_or_target_reads_that_one_partition()
    {
        var recorder = DynamoRecorder.Create(out var dynamo);
        var log = new DynamoAuditLog(dynamo, new DynamoOptions { TableName = "koleksi-test" }, new FixedClock(At));

        await log.ListAsync(new AuditQuery("staff-1", null, null, null, 10), CancellationToken.None);
        await log.ListAsync(new AuditQuery(null, "listing", "L1", null, 10), CancellationToken.None);

        recorder.Queries.Count.ShouldBe(2);
        recorder.Queries[0].ExpressionAttributeValues[":pk"].S.ShouldBe("AUDIT#ACTOR#staff-1");
        recorder.Queries[1].ExpressionAttributeValues[":pk"].S.ShouldBe("AUDIT#TARGET#listing#L1");
    }

    private sealed class FixedClock(DateTimeOffset at) : IClock
    {
        public DateTimeOffset UtcNow => at;
    }
}
