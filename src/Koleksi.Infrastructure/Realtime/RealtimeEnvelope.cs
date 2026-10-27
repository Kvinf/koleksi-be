using System.Text.Json;
using System.Text.Json.Serialization;
using Koleksi.Application.Abstractions;

namespace Koleksi.Infrastructure.Realtime;

/// <summary>The one frame shape the browser receives, whichever transport delivered it.</summary>
/// <remarks>
/// Public so the in-process development hub in <c>Koleksi.Api</c> speaks exactly what the
/// deployed broadcaster does; a second encoder would be a second thing to keep in step.
/// </remarks>
public static class RealtimeEnvelope
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// <c>{"topic":"auction:…","type":"price","data":{…}}</c>. A user topic is named just
    /// <c>user</c>, so a user id never reaches a browser.
    /// </summary>
    public static byte[] Encode(string topic, string type, object data) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new Frame(RealtimeTopics.WireName(topic), type, data), Options);

    private sealed record Frame(string Topic, string Type, object Data);
}
