using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;

namespace Koleksi.Infrastructure.Realtime;

/// <summary>
/// Connect tickets, stored as a hash with a 60-second life and redeemable exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Only the SHA-256 of a ticket is stored, so a read of the table cannot be turned into a
/// connection. Redeeming is a conditional delete that returns the row it removed: two
/// connections presenting the same ticket cannot both succeed, which is the property that
/// makes a ticket seen in a log or a history entry worthless a minute later.
/// </para>
/// <para>
/// The realtime Lambda in <c>koleksi-infra/lambda/realtime</c> redeems tickets too, in a
/// deployed stage, by the same hash and the same keys — see <c>DynamoTable.RealtimeTicketPk</c>.
/// Expiry is checked on redemption as well as by TTL, because DynamoDB removes expired rows
/// lazily, up to a couple of days late.
/// </para>
/// </remarks>
internal sealed class DynamoRealtimeTicketStore(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
    : IRealtimeTicketStore
{
    private const string TicketSk = "#TICKET";
    private const string UserIdAttr = "userId";
    private const string TtlAttr = "expiresAt";

    /// <summary>The longest string accepted as a ticket; anything longer cannot be one of ours.</summary>
    private const int MaxTicketLength = 128;

    public async Task<string> IssueAsync(string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));

        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = options.TableName,
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.RealtimeTicketPk(Hash(ticket))),
                    [DynamoTable.Sk] = Attr.S(TicketSk),
                    [DynamoTable.EntityType] = Attr.S("RealtimeTicket"),
                    [UserIdAttr] = Attr.S(userId),
                    [TtlAttr] = Attr.N(DynamoTable.ToTtl(clock.UtcNow + IRealtimeTicketStore.Lifetime)),
                },
            },
            ct).ConfigureAwait(false);

        return ticket;
    }

    public async Task<string?> ConsumeAsync(string? ticket, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > MaxTicketLength)
        {
            return null;
        }

        DeleteItemResponse deleted;
        try
        {
            deleted = await dynamo.DeleteItemAsync(
                new DeleteItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(DynamoTable.RealtimeTicketPk(Hash(ticket))),
                        [DynamoTable.Sk] = Attr.S(TicketSk),
                    },
                    ConditionExpression = $"attribute_exists({DynamoTable.Pk})",
                    ReturnValues = ReturnValue.ALL_OLD,
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // Unknown, or already redeemed by someone else a moment ago.
            return null;
        }

        var item = deleted.Attributes;
        if (item is null
            || !item.TryGetValue(UserIdAttr, out var userId)
            || !item.TryGetValue(TtlAttr, out var expiresAt)
            || !long.TryParse(expiresAt.N, out var expiresAtSeconds)
            || expiresAtSeconds <= clock.UtcNow.ToUnixTimeSeconds())
        {
            return null;
        }

        return userId.S;
    }

    private static string Hash(string ticket) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
