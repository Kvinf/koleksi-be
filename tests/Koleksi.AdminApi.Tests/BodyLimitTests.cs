using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Koleksi.AdminApi.Tests;

/// <summary>A request body larger than anything staff send is refused before it is read, parsed or acted on.</summary>
public sealed class BodyLimitTests
{
    private const int Cap = 64 * 1024;

    private const string NotePath = "/api/admin/disputes/0000000000000000000000000A/notes";

    private static string JsonOfExactly(int bytes)
    {
        const string Head = "{\"text\":\"";
        const string Tail = "\"}";
        return Head + new string('x', bytes - Head.Length - Tail.Length) + Tail;
    }

    [Fact]
    public async Task A_body_over_the_cap_is_413_with_its_code_and_nothing_happens()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsync(NotePath, new StringContent(JsonOfExactly(Cap + 1), Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("request.too_large");
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_of_exactly_the_cap_gets_past_the_limit_and_meets_the_ordinary_rules()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsync(NotePath, new StringContent(JsonOfExactly(Cap), Encoding.UTF8, "application/json"));

        // Past the size check, so the note's own rules answer: an unknown dispute here, and in any case not a 413.
        response.StatusCode.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_counted_as_it_is_read_and_cut_off()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonOfExactly(Cap * 3)));
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, NotePath) { Content = content };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        factory.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_outsider_with_a_huge_body_still_sees_only_404()
    {
        // The size check sits behind the gate, so a stranger cannot tell this API exists from a 413.
        using var factory = new AdminApiFactory();
        using var outsider = factory.CreateClient();

        using var response = await outsider.PostAsync(NotePath, new StringContent(JsonOfExactly(Cap * 4), Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_refusal_for_size_carries_the_strict_headers_too()
    {
        using var factory = new AdminApiFactory();
        using var client = factory.CreateStaffClient();

        using var response = await client.PostAsync(NotePath, new StringContent(JsonOfExactly(Cap + 1), Encoding.UTF8, "application/json"));

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }
}
