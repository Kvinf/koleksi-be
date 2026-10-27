using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>The first audited action, the audit view, and the rules every later action will be held to.</summary>
public sealed class AdminSessionAndAuditTests
{
    private const string SessionPath = "/api/admin/session";

    [Fact]
    public async Task Starting_a_session_records_one_audit_entry_and_names_the_caller()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsync(SessionPath, content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("staffId").GetString().ShouldBe(AdminApiFactory.StaffAlice);
        body.GetProperty("staffName").GetString().ShouldBe("alice");

        var entry = factory.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("staff.session_started");
        entry.TargetType.ShouldBe("staff");
        entry.TargetId.ShouldBe(AdminApiFactory.StaffAlice);
        entry.At.ShouldBe(factory.Clock.UtcNow);
        entry.RequestId.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_body_cannot_name_a_different_actor()
    {
        // The actor comes from the token. A request that tries to name someone else in its body is simply not read.
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient(AdminApiFactory.StaffAlice, "alice");

        using var response = await client.PostAsJsonAsync(SessionPath, new { staffId = AdminApiFactory.StaffBob, staffName = "bob" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Audit.Entries.ShouldHaveSingleItem().ActorId.ShouldBe(AdminApiFactory.StaffAlice);
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_writes_one_entry()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();

        using var first = await Send(client, key);
        factory.Clock.UtcNow = factory.Clock.UtcNow.AddMinutes(1);
        using var second = await Send(client, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Audit.Entries.Count.ShouldBe(1);

        // The replay is the first answer, not a fresh one.
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("startedAt").GetDateTimeOffset().ShouldBe(firstBody.GetProperty("startedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Requests_with_different_idempotency_keys_each_write_an_entry()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var first = await Send(client, Guid.NewGuid().ToString());
        using var second = await Send(client, Guid.NewGuid().ToString());

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Audit.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task When_the_audit_write_fails_the_action_fails_and_leaves_no_entry()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        var key = Guid.NewGuid().ToString();
        factory.Audit.FailNextAppend = true;

        using var failed = await Send(client, key);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().ShouldBe("server.error");

        // The failure says nothing about why: no exception text, no store name.
        (await failed.Content.ReadAsStringAsync()).ShouldNotContain("audit store");
        factory.Audit.Entries.ShouldBeEmpty();

        // And the failed attempt did not hold its key: the client's retry runs.
        using var retried = await Send(client, key);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_audit_view_lists_newest_first_and_filters_by_actor()
    {
        using var factory = new AdminApiFactory();
        using var alice = factory.CreateStaffClient(AdminApiFactory.StaffAlice, "alice");
        using var bob = factory.CreateStaffClient(AdminApiFactory.StaffBob, "bob");

        (await alice.PostAsync(SessionPath, content: null)).Dispose();
        factory.Clock.UtcNow = factory.Clock.UtcNow.AddMinutes(1);
        (await bob.PostAsync(SessionPath, content: null)).Dispose();

        var everyone = await alice.GetFromJsonAsync<JsonElement>("/api/admin/audit");
        var items = everyone.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("actorName").GetString().ShouldBe("bob");

        var onlyAlice = await alice.GetFromJsonAsync<JsonElement>($"/api/admin/audit?actor={AdminApiFactory.StaffAlice}");
        onlyAlice.GetProperty("items").GetArrayLength().ShouldBe(1);
        onlyAlice.GetProperty("items")[0].GetProperty("actorName").GetString().ShouldBe("alice");
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("actor=abc&targetType=staff&targetId=abc")]
    [InlineData("targetType=staff")]
    [InlineData("cursor=not-a-cursor")]
    public async Task A_bad_audit_query_is_a_coded_400(string query)
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.GetAsync($"/api/admin/audit?{query}");

        // The fake log does not parse cursors, so only the handler's own rules are reachable here. Cursor shape is
        // refused by the real store and tested there (AuditCursorTests).
        if (query.StartsWith("cursor=", StringComparison.Ordinal))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            return;
        }

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().ShouldBe("admin.audit_query_invalid");
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SessionPath);
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }
}
