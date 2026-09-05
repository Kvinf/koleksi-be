using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Notifications;

/// <summary>
/// Sends email-verification codes via SES, tracking the outstanding challenge in the same
/// DynamoDB table everything else is stored in.
/// </summary>
/// <remarks>
/// The same shape as <see cref="SnsPhoneVerificationService"/>, and for the same reason:
/// SES only sends a message, it does not remember what it sent, so this type is the whole
/// of that missing half — it mints the code, stores it with a TTL, and is the only thing
/// that ever reads it back. One challenge per address, not per session: a request always
/// overwrites whatever was outstanding.
/// </remarks>
internal sealed partial class SesEmailVerificationService(
    IAmazonSimpleEmailServiceV2 ses,
    IAmazonDynamoDB dynamo,
    DynamoOptions dynamoOptions,
    EmailOptions emailOptions,
    IClock clock,
    ILogger<SesEmailVerificationService> logger) : IEmailVerificationService
{
    private const string CodeAttr = "code";
    private const string TtlAttr = "expiresAt";

    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public async Task<string?> RequestCodeAsync(string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalized = email.Trim().ToLowerInvariant();
        var code = GenerateCode();
        var expiresAt = clock.UtcNow + CodeLifetime;

        // Unconditional put: replaces any still-outstanding challenge for this address,
        // per the "one challenge per address" reasoning above.
        await dynamo.PutItemAsync(
            new PutItemRequest
            {
                TableName = dynamoOptions.TableName,
                Item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.EmailOtpPkValue(normalized)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.EmailOtpSk()),
                    [DynamoTable.EntityType] = Attr.S("EmailOtp"),
                    [CodeAttr] = Attr.S(code),
                    [TtlAttr] = Attr.N(DynamoTable.ToTtl(expiresAt)),
                },
            },
            ct).ConfigureAwait(false);

        try
        {
            await ses.SendEmailAsync(
                new SendEmailRequest
                {
                    FromEmailAddress = emailOptions.FromAddress,
                    Destination = new Destination { ToAddresses = [normalized] },
                    Content = new EmailContent
                    {
                        Simple = new Message
                        {
                            Subject = new Content { Data = "Kode verifikasi Koleksi.id" },
                            Body = new Body
                            {
                                Text = new Content
                                {
                                    Data = $"Kode verifikasi Koleksi.id: {code}. Berlaku 5 menit.",
                                },
                            },
                        },
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (AmazonSimpleEmailServiceV2Exception)
        {
            // The challenge is already stored regardless — a transient SES failure should
            // not stop the reader from pressing "resend" and getting a fresh attempt, same
            // reasoning as the SNS phone path.
            LogSendFailed(logger);
        }

        // Never the code — SES delivered it (or tried to); this process does not get to
        // see it again, the same boundary the phone and email-OTP-sign-in paths hold.
        return null;
    }

    public async Task<bool> VerifyCodeAsync(string email, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var normalized = email.Trim().ToLowerInvariant();

        // One-shot: deleted on every verify attempt regardless of outcome, so a captured
        // code cannot be replayed even within its five-minute window.
        var response = await dynamo.DeleteItemAsync(
            new DeleteItemRequest
            {
                TableName = dynamoOptions.TableName,
                Key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
                {
                    [DynamoTable.Pk] = Attr.S(DynamoTable.EmailOtpPkValue(normalized)),
                    [DynamoTable.Sk] = Attr.S(DynamoTable.EmailOtpSk()),
                },
                ReturnValues = ReturnValue.ALL_OLD,
            },
            ct).ConfigureAwait(false);

        if (response.Attributes is not { Count: > 0 } item)
        {
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(item.GetLong(TtlAttr));
        if (expiresAt < clock.UtcNow)
        {
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(item.GetString(CodeAttr)),
            System.Text.Encoding.UTF8.GetBytes(code));
    }

    private static string GenerateCode() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SES rejected an email-verification send. The challenge is stored regardless.")]
    private static partial void LogSendFailed(ILogger logger);
}
