using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using Koleksi.Application.Abstractions;
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
/// of that missing half — it mints the code, stores it with a TTL (see
/// <see cref="DynamoOtpChallengeStore"/>), and is the only thing that ever reads it back.
/// One challenge per account and address, not per session: a request always overwrites
/// whatever that account had outstanding for the address.
/// </remarks>
internal sealed partial class SesEmailVerificationService(
    IAmazonSimpleEmailServiceV2 ses,
    DynamoOtpChallengeStore challenges,
    EmailOptions emailOptions,
    ILogger<SesEmailVerificationService> logger) : IEmailVerificationService
{
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);

    public async Task<string?> RequestCodeAsync(string ownerId, string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalized = email.Trim().ToLowerInvariant();
        var code = GenerateCode();

        await challenges.StoreAsync(
            DynamoTable.EmailOtpPkValue(ownerId, normalized), DynamoTable.EmailOtpSk(), "EmailOtp",
            code, CodeLifetime, ct).ConfigureAwait(false);

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

    public Task<bool> VerifyCodeAsync(string ownerId, string email, string code, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return challenges.VerifyAsync(
            DynamoTable.EmailOtpPkValue(ownerId, email.Trim().ToLowerInvariant()), DynamoTable.EmailOtpSk(), code, ct);
    }

    private static string GenerateCode() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "SES rejected an email-verification send. The challenge is stored regardless.")]
    private static partial void LogSendFailed(ILogger logger);
}
