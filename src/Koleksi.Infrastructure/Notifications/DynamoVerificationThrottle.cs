using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;

namespace Koleksi.Infrastructure.Notifications;

/// <summary>
/// <see cref="IVerificationThrottle"/> as two kinds of small row per account, both written with
/// conditional writes so concurrent requests cannot both slip under a limit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cooldown:</b> one row per (account, destination), claimed with a conditional put that
/// succeeds only when there is none or the last has run out. <b>Hourly cap:</b> one counter row per
/// account per clock hour, incremented only while below the cap. A fixed clock hour rather than a
/// sliding window: it is a single atomic update, and the cooldown already spaces requests inside
/// the hour.
/// </para>
/// <para>
/// Expiry is checked against the clock in the condition, not left to DynamoDB's TTL sweep (which
/// can lag by days); <c>expiresAt</c> is still the table's TTL attribute, so the rows tidy themselves.
/// </para>
/// </remarks>
internal sealed class DynamoVerificationThrottle(IAmazonDynamoDB dynamo, DynamoOptions options, IClock clock)
    : IVerificationThrottle
{
    private const string TtlAttr = "expiresAt";
    private const string CountAttr = "count";

    public async Task<VerificationThrottleResult> TryAcquireAsync(
        string userId, string destination, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var now = clock.UtcNow;
        var pk = DynamoTable.OtpThrottlePk(userId);

        // Cooldown first, so a request refused for being too soon does not also spend an hourly slot.
        try
        {
            await dynamo.PutItemAsync(
                new PutItemRequest
                {
                    TableName = options.TableName,
                    Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(pk),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.OtpCooldownSk(HashOf(destination))),
                        [DynamoTable.EntityType] = Attr.S("OtpCooldown"),
                        [TtlAttr] = Attr.N(DynamoTable.ToTtl(now + VerificationLimits.Cooldown)),
                    },
                    ConditionExpression = $"attribute_not_exists({DynamoTable.Pk}) OR #exp <= :now",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#exp"] = TtlAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":now"] = Attr.N(now.ToUnixTimeSeconds()),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            return VerificationThrottleResult.Cooldown;
        }

        // Kept for an hour past the window it counts, so a late sweep can never resurrect a count.
        var hourEnds = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);

        try
        {
            await dynamo.UpdateItemAsync(
                new UpdateItemRequest
                {
                    TableName = options.TableName,
                    Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [DynamoTable.Pk] = Attr.S(pk),
                        [DynamoTable.Sk] = Attr.S(DynamoTable.OtpHourSk(now)),
                    },
                    UpdateExpression = "SET #exp = :ttl, #entity = :entity ADD #count :one",
                    ConditionExpression = "attribute_not_exists(#count) OR #count < :max",
                    ExpressionAttributeNames = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["#exp"] = TtlAttr,
                        ["#entity"] = DynamoTable.EntityType,
                        ["#count"] = CountAttr,
                    },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                    {
                        [":ttl"] = Attr.N(DynamoTable.ToTtl(hourEnds.AddHours(1))),
                        [":entity"] = Attr.S("OtpHourCount"),
                        [":one"] = Attr.N(1),
                        [":max"] = Attr.N(VerificationLimits.MaxCodesPerHour),
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (ConditionalCheckFailedException)
        {
            return VerificationThrottleResult.HourlyLimit;
        }

        return VerificationThrottleResult.Allowed;
    }

    /// <summary>A destination is an email address or phone number: hashed, so neither sits in a key.</summary>
    private static string HashOf(string destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination.Trim().ToLowerInvariant())))[..32];
}
