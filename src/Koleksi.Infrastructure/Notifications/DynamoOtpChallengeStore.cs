using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;

namespace Koleksi.Infrastructure.Notifications;

/// <summary>
/// The outstanding verification challenge for one (account, destination), shared by the SMS and
/// email adapters so the guess-counting rule exists once.
/// </summary>
/// <remarks>
/// <para>
/// <b>A wrong guess does not delete the challenge.</b> It is counted, and only after
/// <see cref="VerificationLimits.MaxAttempts"/> of them is the challenge invalidated. The earlier
/// "delete on every attempt" rule was meant to stop replays, but it let anyone submit one wrong
/// code and wipe somebody else's pending one. The challenge is now also keyed by the account that
/// asked for it, so a stranger's guesses never reach a victim's challenge at all.
/// </para>
/// <para>
/// The attempt is counted <i>before</i> the code is compared, in the same conditional write, so
/// concurrent guesses are each counted and cannot all slip in under the cap.
/// </para>
/// </remarks>
internal sealed class DynamoOtpChallengeStore(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
{
    private const string CodeAttr = "code";
    private const string AttemptsAttr = "attempts";
    private const string TtlAttr = "expiresAt";

    /// <summary>Stores a fresh challenge, replacing this account's previous one for the same destination.</summary>
    public async Task StoreAsync(
        string pk, string sk, string entityType, string code, TimeSpan lifetime, CancellationToken ct)
    {
        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = options.TableName,
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(pk),
                    [DynamoTable.Sk] = Attr.S(sk),
                    [DynamoTable.EntityType] = Attr.S(entityType),
                    [CodeAttr] = Attr.S(code),
                    [AttemptsAttr] = Attr.N(0),
                    [TtlAttr] = Attr.N(DynamoTable.ToTtl(clock.UtcNow + lifetime)),
                },
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>Counts one attempt and compares. A right code is spent; a wrong one is only counted.</summary>
    public async Task<bool> VerifyAsync(string pk, string sk, string code, CancellationToken ct)
    {
        Dictionary<string, AttributeValue> key = new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(pk),
            [DynamoTable.Sk] = Attr.S(sk),
        };

        UpdateItemResponse counted;
        try
        {
            counted = await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = key,
                    UpdateExpression = "ADD #attempts :one",

                    // Present, not expired (the TTL sweep can lag by days, so expiry is checked by
                    // hand), and still under the attempt cap.
                    ConditionExpression =
                        $"attribute_exists({DynamoTable.Pk}) AND #exp > :now "
                        + "AND (attribute_not_exists(#attempts) OR #attempts < :max)",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#attempts"] = AttemptsAttr,
                        ["#exp"] = TtlAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":one"] = Attr.N(1),
                        [":now"] = Attr.N(clock.UtcNow.ToUnixTimeSeconds()),
                        [":max"] = Attr.N(VerificationLimits.MaxAttempts),
                    },
                    ReturnValues = ReturnValue.ALL_NEW,
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            // No challenge, an expired one, or one whose guesses are used up.
            return false;
        }

        var item = counted.Attributes;
        var matches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(item.GetString(CodeAttr)), Encoding.UTF8.GetBytes(code));

        if (matches || item.GetLongOrDefault(AttemptsAttr) >= VerificationLimits.MaxAttempts)
        {
            // Spent by a right code, or invalidated by the last allowed wrong one.
            await dynamo.DeleteItemAsync(
                new DeleteItemRequest { TableName = options.TableName, Key = key }, ct).ConfigureAwait(false);
        }

        return matches;
    }
}
