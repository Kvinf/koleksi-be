using Amazon.ApiGatewayManagementApi;
using Amazon.ApiGatewayManagementApi.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Realtime;

/// <summary>
/// Delivers an event to every connection subscribed to a topic, through API Gateway's
/// WebSocket API.
/// </summary>
/// <remarks>
/// <para>
/// Subscriptions are rows in the main table (<c>WS#TOPIC#{topic}</c>, one <c>CONN#{id}</c>
/// each), written by the realtime Lambda when a browser subscribes. A publish queries that
/// partition and posts the frame to each connection, a few at a time.
/// </para>
/// <para>
/// <b>Cost.</b> One strongly-consistent query plus one API Gateway message per subscriber, at
/// about $1 per million messages. Nothing runs while nobody is subscribed. The query is
/// consistent on purpose: a bidder who subscribed a moment ago must not be missed because the
/// index had not caught up.
/// </para>
/// <para>
/// <b>Bounded.</b> At most <see cref="MaxConnectionsPerPublish"/> connections are served per
/// publish, and a stale one (the browser went away without saying so) is dropped from the
/// topic when API Gateway answers 410 Gone. Rows left behind by a crashed client expire by TTL.
/// </para>
/// </remarks>
internal sealed partial class ApiGatewayRealtimeBroadcaster(
    IAmazonDynamoDB dynamo,
    DynamoOptions options,
    IAmazonApiGatewayManagementApi gateway,
    ILogger<ApiGatewayRealtimeBroadcaster> logger) : IRealtimeBroadcaster
{
    /// <summary>
    /// A ceiling on one publish, so a single hot topic cannot hold a Lambda for the whole of
    /// its timeout. Past it, the remainder rely on their client's reconnect and fallback.
    /// </summary>
    internal const int MaxConnectionsPerPublish = 2_000;

    private const int PostConcurrency = 16;

    public async Task PublishAsync(string topic, string type, object data, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(data);

        var connectionIds = await ListConnectionsAsync(topic, ct).ConfigureAwait(false);
        if (connectionIds.Count == 0)
        {
            return;
        }

        var frame = RealtimeEnvelope.Encode(topic, type, data);

        await Parallel.ForEachAsync(
            connectionIds,
            new ParallelOptions { MaxDegreeOfParallelism = PostConcurrency, CancellationToken = ct },
            async (connectionId, token) =>
            {
                try
                {
                    await gateway.PostToConnectionAsync(
                        new PostToConnectionRequest { ConnectionId = connectionId, Data = new MemoryStream(frame) },
                        token).ConfigureAwait(false);
                }
                catch (GoneException)
                {
                    await ForgetAsync(topic, connectionId, token).ConfigureAwait(false);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // One bad connection must not stop the others from being told.
                    LogPostFailed(logger, topic, failure);
                }
            }).ConfigureAwait(false);
    }

    private async Task<List<string>> ListConnectionsAsync(string topic, CancellationToken ct)
    {
        var ids = new List<string>();
        var skPrefix = DynamoTable.RealtimeConnectionSkPrefixValue();
        Dictionary<string, AttributeValue>? startKey = null;

        do
        {
            var page = await dynamo.QueryAsync(
                new QueryRequest
                {
                    TableName = options.TableName,
                    ConsistentRead = true,
                    KeyConditionExpression = "#pk = :pk AND begins_with(#sk, :prefix)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#pk"] = DynamoTable.Pk,
                        ["#sk"] = DynamoTable.Sk,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":pk"] = Attr.S(DynamoTable.RealtimeTopicPk(topic)),
                        [":prefix"] = Attr.S(skPrefix),
                    },
                    ProjectionExpression = "#sk",
                    ExclusiveStartKey = startKey,
                },
                ct).ConfigureAwait(false);

            foreach (var item in page.Items)
            {
                ids.Add(item[DynamoTable.Sk].S[skPrefix.Length..]);
                if (ids.Count >= MaxConnectionsPerPublish)
                {
                    return ids;
                }
            }

            startKey = page.LastEvaluatedKey is { Count: > 0 } ? page.LastEvaluatedKey : null;
        }
        while (startKey is not null);

        return ids;
    }

    private async Task ForgetAsync(string topic, string connectionId, CancellationToken ct)
    {
        try
        {
            await dynamo.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.RealtimeTopicPk(topic)),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.RealtimeConnectionSk(connectionId)),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Housekeeping only; the row's TTL collects it regardless.
            LogForgetFailed(logger, topic, failure);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Posting a live update for topic {Topic} to one connection failed.")]
    private static partial void LogPostFailed(ILogger logger, string topic, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dropping a gone connection from topic {Topic} failed; its TTL will collect it.")]
    private static partial void LogForgetFailed(ILogger logger, string topic, Exception exception);
}
