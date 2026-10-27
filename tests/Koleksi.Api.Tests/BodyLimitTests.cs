using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>A request body larger than anything the site sends is refused before it is read, parsed or acted on.</summary>
public sealed class BodyLimitTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const int Cap = 256 * 1024;

    private const string Path = "/api/cart/items";

    private static string JsonOfExactly(int bytes)
    {
        const string Head = "{\"listingId\":\"";
        const string Tail = "\"}";
        return Head + new string('x', bytes - Head.Length - Tail.Length) + Tail;
    }

    [Fact]
    public async Task A_body_over_the_cap_is_413_with_its_code_even_before_sign_in_is_checked()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync(Path, new StringContent(JsonOfExactly(Cap + 1), Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString().ShouldBe("request.too_large");
    }

    [Fact]
    public async Task A_body_of_exactly_the_cap_is_not_refused_for_its_size()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync(Path, new StringContent(JsonOfExactly(Cap), Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldNotBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_is_cut_off_at_the_cap()
    {
        using var client = factory.CreateClientAs("sub-body-limit", "body-limit");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonOfExactly(Cap * 2)));
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentLength = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = content };
        request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_refusal_for_size_still_carries_the_security_headers()
    {
        using var anonymous = factory.CreateClient();

        using var response = await anonymous.PostAsync(Path, new StringContent(JsonOfExactly(Cap + 1), Encoding.UTF8, "application/json"));

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
    }
}
