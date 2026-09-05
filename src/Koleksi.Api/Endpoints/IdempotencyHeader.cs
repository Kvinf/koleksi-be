namespace Koleksi.Api.Endpoints;

/// <summary>Reads the optional client-supplied double-submit key, the same way everywhere.</summary>
internal static class IdempotencyHeader
{
    private const string Name = "Idempotency-Key";

    /// <summary>
    /// Returns the header value, or <c>null</c> when absent, blank, or implausibly long.
    /// </summary>
    /// <remarks>
    /// The length cap is defensive, not functional: the key is a UUID from
    /// <c>crypto.randomUUID()</c> on the frontend, so anything far longer than that is not
    /// a key a real client sent — treating it as absent rather than passing an
    /// attacker-sized string into the DynamoDB key it becomes part of.
    /// </remarks>
    public static string? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var value = request.Headers[Name].ToString();
        return string.IsNullOrWhiteSpace(value) || value.Length > 128 ? null : value;
    }
}
