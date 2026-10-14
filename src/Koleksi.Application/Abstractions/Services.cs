using Koleksi.Domain.Common;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Issues short-lived, single-purpose upload grants for user media.
/// </summary>
/// <remarks>
/// <para>
/// Photos never pass through the API. The client asks for a grant, uploads straight to
/// object storage, and sends us back only the object key. Streaming multi-megabyte
/// images through Lambda would be slow, expensive, and would put attacker-controlled
/// bytes inside our execution environment for no benefit.
/// </para>
/// <para>
/// The grant is what makes that safe. The implementation pins the content type, caps
/// the content length, and mints the key itself, so a client cannot choose where its
/// bytes land, how large they are, or what the bucket will later serve them as.
/// </para>
/// </remarks>
public interface IMediaStorage
{
    /// <summary>Content types accepted for user-uploaded photos.</summary>
    /// <remarks>
    /// An allowlist, never a blocklist. SVG is excluded on purpose: it is a document
    /// format that executes script, and serving one from our own origin would be a
    /// stored cross-site scripting hole.
    /// </remarks>
    static IReadOnlySet<string> PermittedImageTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/avif",
    };

    /// <summary>Largest upload accepted, in bytes.</summary>
    const long MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Creates a time-limited grant to upload one object.
    /// </summary>
    /// <param name="purpose">What the object is for, which decides the key prefix.</param>
    /// <param name="ownerId">The uploading user, embedded in the key for attribution.</param>
    /// <param name="contentType">Declared type; rejected unless in <see cref="PermittedImageTypes"/>.</param>
    /// <param name="contentLength">Declared size; rejected above <see cref="MaxUploadBytes"/>.</param>
    Task<UploadGrant> CreateUploadGrantAsync(
        MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct);

    /// <summary>
    /// Grants three uploads at once — original, large, and small — sharing one storage key
    /// prefix, for a listing photo.
    /// </summary>
    /// <remarks>
    /// Listing photos are the one media purpose rendered at genuinely different sizes in
    /// genuinely different places: a ~40px dashboard row, a browse/item-card grid, and a
    /// full-bleed detail-page hero. Serving the multi-megabyte original everywhere would
    /// waste bandwidth on the two smaller spots; generating derivatives is done client-side
    /// (see <c>directUpload.ts</c>'s canvas resize) rather than by a server-side pipeline,
    /// so this grants all three uploads in one round trip instead of three separate ones
    /// that would otherwise need their own correlated key.
    /// </remarks>
    Task<PhotoUploadGrantSet> CreatePhotoUploadGrantSetAsync(
        string ownerId, PhotoUploadSpec original, PhotoUploadSpec large, PhotoUploadSpec small, CancellationToken ct);

    /// <summary>
    /// Confirms an object exists, is within limits, and really is the image type it claimed.
    /// </summary>
    /// <remarks>
    /// Called before a key is attached to a listing or a shipping proof. The grant
    /// constrains the request; this verifies the result. Without it a client could take
    /// a grant for a JPEG and upload something else entirely under that key.
    /// </remarks>
    Task<bool> VerifyUploadedAsync(string storageKey, CancellationToken ct);

    /// <summary>
    /// The three-object equivalent of <see cref="VerifyUploadedAsync"/>, for a key minted by
    /// <see cref="CreatePhotoUploadGrantSetAsync"/> — true only when original, large, and
    /// small all landed.
    /// </summary>
    Task<bool> VerifyPhotoUploadedAsync(string storageKey, CancellationToken ct);

    /// <summary>
    /// Builds the public CDN URL for a stored object.
    /// </summary>
    /// <param name="size">
    /// Ignored for every purpose but <see cref="MediaPurpose.ListingPhoto"/>, whose key is a
    /// prefix shared by three actual objects rather than one — see
    /// <see cref="CreatePhotoUploadGrantSetAsync"/>. Defaults to <see cref="PhotoSize.Original"/>.
    /// </param>
    string PublicUrl(string storageKey, PhotoSize size = PhotoSize.Original);
}

/// <summary>
/// Which of a listing photo's three uploaded sizes a URL should point at — see
/// <see cref="IMediaStorage.CreatePhotoUploadGrantSetAsync"/> for where the three come from.
/// </summary>
public enum PhotoSize
{
    /// <summary>The untouched upload — the item detail page's hero image.</summary>
    Original = 0,

    /// <summary>Resized client-side, ~1200px longest edge — browse grid and item cards.</summary>
    Large = 1,

    /// <summary>Resized client-side, ~320px longest edge — the dashboard's row thumbnail.</summary>
    Small = 2,
}

/// <summary>What one of the three uploads in a <see cref="PhotoUploadGrantSet"/> will be.</summary>
public sealed record PhotoUploadSpec(string ContentType, long ContentLength);

/// <summary>
/// Three upload grants sharing one <see cref="StorageKey"/> prefix — see
/// <see cref="IMediaStorage.CreatePhotoUploadGrantSetAsync"/>.
/// </summary>
public sealed record PhotoUploadGrantSet(
    string StorageKey, UploadGrant Original, UploadGrant Large, UploadGrant Small);

/// <summary>What an uploaded object is for. Decides its key prefix and retention.</summary>
public enum MediaPurpose
{
    ListingPhoto = 0,
    Avatar = 1,
    ShippingProof = 2,
    ReviewPhoto = 3,

    /// <summary>
    /// Identity documents. Written to a separate, private bucket that the CDN cannot
    /// reach — these are never served publicly under any circumstances.
    /// </summary>
    IdentityDocument = 4,
}

/// <summary>A grant to upload exactly one object.</summary>
/// <param name="StorageKey">The key the object must be written to. Chosen by the server.</param>
/// <param name="UploadUrl">Presigned URL the client PUTs to.</param>
/// <param name="RequiredHeaders">Headers the client must send for the signature to match.</param>
/// <param name="ExpiresAt">When the grant stops working.</param>
public sealed record UploadGrant(
    string StorageKey,
    Uri UploadUrl,
    IReadOnlyDictionary<string, string> RequiredHeaders,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Sends a one-time code to a phone number by SMS and checks what comes back.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not modelled on Cognito's email-OTP flow, even though the shape looks
/// similar: Cognito owns the challenge/session state for that flow entirely, and this
/// service does not have an equivalent managed identity provider standing behind it, so
/// implementations must persist the outstanding challenge themselves between the request
/// and verify calls — there is no session token that already means something to a second
/// service the way Cognito's does.
/// </para>
/// <para>
/// Anti-enumeration is a smaller concern here than it is for sign-in: a phone number is
/// not a login identifier, and confirming "we sent a code to that number" leaks nothing an
/// attacker could not already learn by texting it themselves.
/// </para>
/// </remarks>
public interface IPhoneVerificationService
{
    /// <summary>
    /// Sends a fresh code to <paramref name="phone"/>, replacing any still outstanding for this account.
    /// </summary>
    /// <param name="ownerId">
    /// The account asking. The pending challenge belongs to it: nobody else's guesses can touch or
    /// invalidate it.
    /// </param>
    /// <returns>
    /// The code itself — present only when the local development stub issues this,
    /// because local development has no SMS gateway to deliver it to. The production SNS
    /// adapter never returns one; the phone receives it and this process never sees it
    /// again. Same shape and same reasoning as <c>OtpChallenge.DevCode</c> on the email-OTP
    /// sign-in path.
    /// </returns>
    Task<string?> RequestCodeAsync(string ownerId, string phone, CancellationToken ct);

    /// <summary>
    /// Checks the submitted code against the one this account last asked to have sent to this number.
    /// </summary>
    /// <remarks>
    /// A right code is spent. A wrong code does <b>not</b> delete the pending one: it counts as an
    /// attempt, and only after <see cref="VerificationLimits.MaxAttempts"/> of them is it invalidated.
    /// </remarks>
    Task<bool> VerifyCodeAsync(string ownerId, string phone, string code, CancellationToken ct);
}

/// <summary>
/// Sends a one-time code to an email address and checks what comes back.
/// </summary>
/// <remarks>
/// The profile's phone-verification step falls back to this when the reader has not
/// entered a phone number yet — proving they hold the account's own mailbox is still proof
/// of reachability, just not specifically an SMS-capable one. Shaped identically to
/// <see cref="IPhoneVerificationService"/> on purpose: same request/verify/one-shot
/// contract, same reason a real implementation needs its own durable challenge storage
/// (there is no managed identity provider holding this state for us the way Cognito does
/// for email-OTP sign-in).
/// </remarks>
public interface IEmailVerificationService
{
    /// <summary>
    /// Sends a fresh code to <paramref name="email"/>, replacing any still outstanding.
    /// </summary>
    /// <returns>
    /// The code itself — present only when the local development stub issues this, for the
    /// same reason <see cref="IPhoneVerificationService.RequestCodeAsync"/>'s does. The
    /// production SES adapter never returns one.
    /// </returns>
    Task<string?> RequestCodeAsync(string ownerId, string email, CancellationToken ct);

    /// <summary>
    /// Checks the submitted code against the one this account last asked to have sent to this
    /// address. Wrong guesses count as attempts and do not delete the pending code, exactly as
    /// <see cref="IPhoneVerificationService.VerifyCodeAsync"/>.
    /// </summary>
    Task<bool> VerifyCodeAsync(string ownerId, string email, string code, CancellationToken ct);
}

/// <summary>
/// Hands checkout off to the third-party payment processor and interprets its callbacks.
/// </summary>
/// <remarks>
/// The design specifies a gateway hand-off with no in-app method picker: the processor
/// owns QRIS, the e-wallets, and virtual accounts. We never see or store a card number,
/// a wallet credential, or a bank token, which keeps the entire cardholder-data problem
/// outside our boundary.
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>Creates a payment covering every order in one checkout.</summary>
    Task<PaymentSession> CreateSessionAsync(PaymentRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies a webhook's authenticity and parses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns <c>null</c> when the signature does not verify — the endpoint then
    /// answers <c>401</c> and records nothing. Callers must never fall back to trusting
    /// the body.
    /// </para>
    /// <para>
    /// Implementations compare signatures in constant time. A byte-by-byte comparison
    /// that returns early leaks, through response timing, how much of a guessed
    /// signature was correct, which is enough to forge one.
    /// </para>
    /// </remarks>
    PaymentNotification? VerifyAndParseWebhook(string rawBody, IReadOnlyDictionary<string, string> headers);
}

/// <param name="ReferenceId">Our own id for the checkout, echoed back on the webhook.</param>
/// <param name="Amount">Total across every order in the checkout.</param>
/// <param name="BuyerEmail">Where the processor sends its receipt.</param>
/// <param name="ExpiresAt">When the payment window closes.</param>
public sealed record PaymentRequest(
    string ReferenceId, Money Amount, string BuyerEmail, DateTimeOffset ExpiresAt);

/// <param name="ReferenceId">Our checkout id.</param>
/// <param name="RedirectUrl">Where to send the buyer to pay.</param>
/// <param name="QrisPayload">QRIS string to render, when the processor returns one.</param>
/// <param name="ExpiresAt">When the session stops accepting payment.</param>
public sealed record PaymentSession(
    string ReferenceId, Uri RedirectUrl, string? QrisPayload, DateTimeOffset ExpiresAt);

/// <summary>A verified payment status change from the processor.</summary>
/// <param name="ReferenceId">Our checkout id.</param>
/// <param name="Status">The new status.</param>
/// <param name="SettledAmount">Amount actually settled, checked against the order total.</param>
/// <param name="ProcessorReference">The processor's own transaction id, for reconciliation.</param>
/// <param name="EventId">Unique event id, used to discard duplicate deliveries.</param>
public sealed record PaymentNotification(
    string ReferenceId,
    PaymentStatus Status,
    Money SettledAmount,
    string ProcessorReference,
    string EventId);

public enum PaymentStatus
{
    Pending = 0,
    Settled = 1,
    Failed = 2,
    Expired = 3,
}

/// <summary>
/// Remembers which side-effecting requests have already been carried out.
/// </summary>
/// <remarks>
/// <para>
/// Two sources of duplicates matter here. Payment webhooks are delivered at least once
/// by design, and a buyer on a flaky mobile connection will retry a checkout they
/// cannot tell succeeded. Neither may result in two charges or two orders.
/// </para>
/// <para>
/// Keys are scoped per user so one caller cannot collide with, or probe for, another's
/// key.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Claims a key, returning <c>false</c> when it was already claimed.
    /// </summary>
    /// <remarks>
    /// A conditional write, not a read-then-write: two concurrent requests carrying the
    /// same key must not both observe "unclaimed" and both proceed. A claim that never got a
    /// result and is older than <see cref="IdempotencyExtensions.InFlightLease"/> counts as
    /// abandoned (its request crashed) and can be claimed again.
    /// </remarks>
    Task<bool> TryClaimAsync(string scope, string key, TimeSpan retention, CancellationToken ct);

    /// <summary>Stores the response so a replay returns the original result.</summary>
    Task RecordResultAsync(string scope, string key, string resultJson, CancellationToken ct);

    /// <summary>Returns a previously recorded response, if any.</summary>
    Task<string?> GetRecordedResultAsync(string scope, string key, CancellationToken ct);

    /// <summary>
    /// Gives a claim back so the same key can run again, when the work failed before a result existed.
    /// </summary>
    /// <remarks>
    /// Does nothing once a result has been recorded: a finished request must keep replaying.
    /// A request that dies without calling this (a crashed or timed-out Lambda) is covered by the
    /// claim's lease instead, see <see cref="IdempotencyExtensions.InFlightLease"/>.
    /// </remarks>
    Task ReleaseClaimAsync(string scope, string key, CancellationToken ct);
}
