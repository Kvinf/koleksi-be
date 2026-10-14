using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace Koleksi.Infrastructure.Notifications;

/// <summary>
/// Sends phone-verification codes over SMS via SNS, tracking the outstanding challenge in
/// the same DynamoDB table everything else is stored in.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the email-OTP flow, there is no managed identity provider on the other end
/// holding challenge state for us — SNS only sends a message, it does not remember what it
/// sent. This type is the whole of that missing half: it mints the code, stores it with a
/// TTL (see <see cref="DynamoOtpChallengeStore"/>), and is the only thing that ever reads it back.
/// </para>
/// <para>
/// One challenge per account and phone number, not per session. A request always
/// overwrites whatever that account had outstanding for the number, so a reader who mistyped
/// a code and asks for a new one invalidates the old one rather than leaving two live codes
/// that would both verify.
/// </para>
/// </remarks>
internal sealed partial class SnsPhoneVerificationService(
    IAmazonSimpleNotificationService sns,
    DynamoOtpChallengeStore challenges,
    ILogger<SnsPhoneVerificationService> logger) : IPhoneVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public async Task<string?> RequestCodeAsync(string ownerId, string phone, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);

        var normalized = NormalizeToE164(phone);
        var code = GenerateCode();

        await challenges.StoreAsync(
            DynamoTable.PhoneOtpPkValue(ownerId, normalized), DynamoTable.PhoneOtpSk(), "PhoneOtp",
            code, CodeLifetime, ct).ConfigureAwait(false);

        try
        {
            await sns.PublishAsync(
                new PublishRequest
                {
                    PhoneNumber = normalized,
                    Message = $"Kode verifikasi Koleksi.id: {code}. Berlaku 5 menit.",

                    // Transactional, not marketing — SNS throttles and prices the two
                    // differently, and a verification code delivered slowly is useless.
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
                    {
                        ["AWS.SNS.SMS.SMSType"] = new MessageAttributeValue
                        {
                            DataType = "String",
                            StringValue = "Transactional",
                        },
                    },
                },
                ct).ConfigureAwait(false);
        }
        catch (AmazonSimpleNotificationServiceException)
        {
            // The challenge is already stored regardless — a transient SNS failure should
            // not stop the reader from pressing "resend" and getting a fresh attempt, and
            // there is nothing more specific to tell them: the failure is on our side, not
            // theirs.
            LogSendFailed(logger);
        }

        // Never the code — SNS delivered it (or tried to); this process does not get to
        // see it again, the same boundary the real Cognito adapter holds for email-OTP.
        return null;
    }

    public Task<bool> VerifyCodeAsync(string ownerId, string phone, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return challenges.VerifyAsync(
            DynamoTable.PhoneOtpPkValue(ownerId, NormalizeToE164(phone)), DynamoTable.PhoneOtpSk(), code, ct);
    }

    /// <summary>
    /// Best-effort normalisation to E.164, assuming an Indonesian number when no country
    /// code is present — the same assumption the sign-up form's phone field makes.
    /// </summary>
    private static string NormalizeToE164(string phone)
    {
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());

        if (phone.TrimStart().StartsWith('+'))
        {
            return "+" + digits;
        }

        if (digits.StartsWith("62", StringComparison.Ordinal))
        {
            return "+" + digits;
        }

        if (digits.StartsWith('0'))
        {
            return "+62" + digits[1..];
        }

        return "+62" + digits;
    }

    private static string GenerateCode() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SNS rejected a phone-verification send. The challenge is stored regardless.")]
    private static partial void LogSendFailed(ILogger logger);
}
