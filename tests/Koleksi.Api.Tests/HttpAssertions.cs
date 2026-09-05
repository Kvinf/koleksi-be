using System.Net.Http.Json;
using System.Text.Json;

namespace Koleksi.Api.Tests;

/// <summary>Reading responses without repeating the same four lines in every test.</summary>
internal static class HttpAssertions
{
    /// <summary>
    /// Parses the body as JSON.
    /// </summary>
    /// <remarks>
    /// The element is cloned so it outlives the <see cref="JsonDocument"/> that produced
    /// it, which keeps the caller from having to hold a disposable just to make one
    /// assertion.
    /// </remarks>
    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The raw body text.
    /// </summary>
    /// <remarks>
    /// Some assertions are about what the response does <b>not</b> contain — a leading
    /// bidder's maximum, an internal exception message. Those have to be made against the
    /// bytes on the wire, because a typed model can only show what someone remembered to
    /// put on it.
    /// </remarks>
    public static Task<string> ReadTextAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Content.ReadAsStringAsync();
    }

    /// <summary>Posts a bid.</summary>
    public static Task<HttpResponseMessage> PostBidAsync(
        this HttpClient client, string listingId, long amountRupiah)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PostAsJsonAsync(
            $"/api/auctions/{listingId}/bids", new { amountRupiah });
    }

    /// <summary>Posts a buyout, with an empty body — the endpoint takes none.</summary>
    public static Task<HttpResponseMessage> PostBuyoutAsync(this HttpClient client, string listingId)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PostAsync($"/api/auctions/{listingId}/buyout", null);
    }
}
