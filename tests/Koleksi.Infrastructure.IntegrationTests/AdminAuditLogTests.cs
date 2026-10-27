using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// The audit log against a real table: the one property a fake cannot vouch for is that DynamoDB applies the change and its
/// audit rows together or not at all.
/// </summary>
public sealed class AdminAuditLogTests : DynamoTestBase
{
    private DynamoOptions Options => new() { TableName = Table.TableName, ServiceUrl = DynamoLocal.ServiceUrl };

    private DynamoAuditLog Log => new(Table.Client, Options, Table.Clock);

    private AuditEntry Entry(string actorId = "staff-1", string targetId = "listing-9", string action = "listing.taken_down", string? reason = "counterfeit")
    {
        var entry = AuditEntry.Create(Table.Clock, actorId, "alice", action, "listing", targetId, "req-" + Guid.NewGuid().ToString("N"), reason);
        Table.Clock.UtcNow = Table.Clock.UtcNow.AddSeconds(1);
        return entry;
    }

    [DynamoLocalFact]
    public async Task An_appended_entry_is_listed_and_found_by_actor_and_by_target()
    {
        var entry = Entry();
        await Log.AppendAsync(entry, Ct);

        (await Log.ListAsync(new AuditQuery(null, null, null, null, 10), Ct)).Items.ShouldBe([entry]);
        (await Log.ListAsync(new AuditQuery("staff-1", null, null, null, 10), Ct)).Items.ShouldBe([entry]);
        (await Log.ListAsync(new AuditQuery(null, "listing", "listing-9", null, 10), Ct)).Items.ShouldBe([entry]);
        (await Log.ListAsync(new AuditQuery("staff-2", null, null, null, 10), Ct)).Items.ShouldBeEmpty();
        (await Log.ListAsync(new AuditQuery(null, "listing", "other", null, 10), Ct)).Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task Entries_come_back_newest_first_and_paging_loses_and_repeats_nothing()
    {
        var written = new List<AuditEntry>();
        for (var i = 0; i < 7; i++)
        {
            var entry = Entry(targetId: $"listing-{i}");
            await Log.AppendAsync(entry, Ct);
            written.Add(entry);
        }

        var seen = new List<AuditEntry>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await Log.ListAsync(new AuditQuery(null, null, null, cursor, 3), Ct);
            seen.AddRange(page.Items);
            cursor = page.NextCursor;
            pages++;
        }
        while (cursor is not null && pages < 10);

        seen.Select(e => e.Id).Distinct().Count().ShouldBe(7);
        seen.ShouldBe([.. written.AsEnumerable().Reverse()]);
    }

    [DynamoLocalFact]
    public async Task The_unfiltered_view_crosses_a_month_boundary()
    {
        Table.Clock.UtcNow = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);
        var september = Entry(targetId: "a");
        await Log.AppendAsync(september, Ct);

        Table.Clock.UtcNow = new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);
        var october = Entry(targetId: "b");
        await Log.AppendAsync(october, Ct);

        var page = await Log.ListAsync(new AuditQuery(null, null, null, null, 10), Ct);

        page.Items.ShouldBe([october, september]);
    }

    [DynamoLocalFact]
    public async Task A_change_and_its_audit_rows_are_written_together()
    {
        var entry = Entry();
        var items = new List<TransactWriteItem>
        {
            new() { Put = new Put { TableName = Table.TableName, Item = Row("SETTING#holiday", "2026-12-25") } },
        };
        items.AddRange(AuditItems.For(entry, Table.TableName));

        await Table.Client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, Ct);

        (await Table.Client.GetItemAsync(Table.TableName, Key("SETTING#holiday", "2026-12-25"), Ct)).Item.ShouldNotBeEmpty();
        (await Log.ListAsync(new AuditQuery(null, null, null, null, 10), Ct)).Items.ShouldBe([entry]);
    }

    [DynamoLocalFact]
    public async Task When_the_change_fails_its_condition_no_audit_row_is_left_behind()
    {
        // The change was already made (the row exists), and this attempt requires that it does not.
        await Table.Client.PutItemAsync(Table.TableName, Row("SETTING#holiday", "2026-12-25"), Ct);

        var entry = Entry();
        var items = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = Table.TableName,
                    Item = Row("SETTING#holiday", "2026-12-25"),
                    ConditionExpression = "attribute_not_exists(pk)",
                },
            },
        };
        items.AddRange(AuditItems.For(entry, Table.TableName));

        await Should.ThrowAsync<TransactionCanceledException>(
            () => Table.Client.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = items }, Ct));

        (await Log.ListAsync(new AuditQuery(null, null, null, null, 10), Ct)).Items.ShouldBeEmpty();
        (await Log.ListAsync(new AuditQuery("staff-1", null, null, null, 10), Ct)).Items.ShouldBeEmpty();
        (await Log.ListAsync(new AuditQuery(null, "listing", "listing-9", null, 10), Ct)).Items.ShouldBeEmpty();
    }

    [DynamoLocalFact]
    public async Task An_existing_audit_row_cannot_be_replaced_through_the_write_path()
    {
        var entry = Entry(reason: "original");
        await Log.AppendAsync(entry, Ct);

        // Same entry id, different content: the shape of an attempt to rewrite history.
        var forged = AuditEntry.Restore(
            entry.Id, entry.At, entry.ActorId, entry.ActorName, entry.Action, entry.TargetType, entry.TargetId,
            entry.RequestId, "rewritten", entry.Before, entry.After);

        await Should.ThrowAsync<TransactionCanceledException>(() => Table.Client.TransactWriteItemsAsync(
            new TransactWriteItemsRequest { TransactItems = [.. AuditItems.For(forged, Table.TableName)] }, Ct));

        (await Log.ListAsync(new AuditQuery(null, null, null, null, 10), Ct)).Items.Single().Reason.ShouldBe("original");
    }

    [DynamoLocalFact]
    public async Task A_malformed_cursor_is_refused_and_reads_nothing()
    {
        var failure = await Should.ThrowAsync<DomainException>(
            () => Log.ListAsync(new AuditQuery(null, null, null, "AUDIT#ACTOR#someone-else", 10), Ct));

        failure.Code.ShouldBe("admin.audit_query_invalid");
    }

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) =>
        new() { ["pk"] = new AttributeValue { S = pk }, ["sk"] = new AttributeValue { S = sk } };

    private static Dictionary<string, AttributeValue> Row(string pk, string sk) =>
        new(Key(pk, sk)) { ["note"] = new AttributeValue { S = "x" } };
}
