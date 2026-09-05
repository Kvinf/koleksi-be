#if DEBUG
using System.Collections.Concurrent;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Development;

/// <summary>
/// An in-memory <see cref="IMediaStorage"/> that serves uploads back from this same
/// process instead of a real bucket.
/// </summary>
/// <remarks>
/// The fallback stand-in, not the default: with <c>Koleksi:Media:ServiceUrl</c> set (as
/// <c>appsettings.Development.json</c> does by default), <c>Program.cs</c> leaves the
/// ordinary <c>S3MediaStorage</c> in place pointed at MinIO instead of registering this
/// type — see <c>docker-compose.yml</c> and <c>InfrastructureServiceCollectionExtensions
/// .BuildS3Client</c>. This type exists for the developer who has not started MinIO:
/// pretending there is nowhere to PUT to would otherwise strand every upload on a
/// presigned URL nothing answers. Gated the same way as the other stand-ins —
/// <c>#if DEBUG</c> plus <c>IsDevelopment()</c>, wired in <c>Program.cs</c> right alongside
/// the phone/email stubs.
///
/// The grant's "signature" is the key itself — a server-chosen, unguessable path under
/// <c>/api/media/local-object</c> — rather than a cryptographically signed URL. That is a
/// smaller guarantee than <c>S3MediaStorage</c>'s, acceptable only because this type is
/// unreachable outside a Debug build; see <c>LocalIdentity</c>'s doc comment for the
/// three-lock pattern this follows. Objects live in memory only: restart the API and every
/// uploaded photo is gone, same as the accounts <c>LocalIdentityService</c> forgets.
/// </remarks>
internal sealed class LocalMediaStorage(IHttpContextAccessor httpContextAccessor) : IMediaStorage
{
    private readonly ConcurrentDictionary<string, StoredObject> objects = new(StringComparer.Ordinal);

    public Task<UploadGrant> CreateUploadGrantAsync(
        MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        // Same allowlist and size cap as the real adapter, checked before anything is
        // handed out — a developer's local behaviour should not accept what a deployed
        // stage would refuse.
        if (!IMediaStorage.PermittedImageTypes.Contains(contentType))
        {
            throw DomainException.For(
                "media.unsupported_type",
                "Photos must be JPEG, PNG, WebP, or AVIF.");
        }

        if (contentLength <= 0 || contentLength > IMediaStorage.MaxUploadBytes)
        {
            throw DomainException.For(
                "media.too_large",
                $"Photos must be under {IMediaStorage.MaxUploadBytes / (1024 * 1024)} MB.");
        }

        var key = BuildKey(purpose, ownerId, contentType);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

        return Task.FromResult(new UploadGrant(
            StorageKey: key,
            UploadUrl: new Uri($"{BaseUrl()}/api/media/local-object/{key}"),
            RequiredHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = contentType,
            },
            ExpiresAt: expiresAt));
    }

    public Task<bool> VerifyUploadedAsync(string storageKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var verified = objects.TryGetValue(storageKey, out var stored)
            && stored.Bytes.Length > 0
            && stored.Bytes.Length <= IMediaStorage.MaxUploadBytes
            && IMediaStorage.PermittedImageTypes.Contains(stored.ContentType);

        return Task.FromResult(verified);
    }

    public string PublicUrl(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        // Same refusal as S3MediaStorage: identity documents have no public URL, here or
        // in a deployed stage.
        if (PurposeFromKey(storageKey) == MediaPurpose.IdentityDocument)
        {
            throw new InvalidOperationException("Identity documents are never served publicly.");
        }

        return $"{BaseUrl()}/api/media/local-object/{storageKey}";
    }

    /// <summary>Records the bytes a PUT to <c>/api/media/local-object/{key}</c> actually sent.</summary>
    internal void Store(string storageKey, string contentType, byte[] bytes) =>
        objects[storageKey] = new StoredObject(contentType, bytes);

    /// <summary>Answers the GET that serves a stored object back.</summary>
    internal bool TryGet(string storageKey, out StoredObject stored) =>
        objects.TryGetValue(storageKey, out stored);

    /// <summary>
    /// The address the browser can reach this process at — taken from the current request
    /// rather than configured, so it is always whatever host and scheme the frontend is
    /// already using to talk to the API.
    /// </summary>
    private string BaseUrl()
    {
        var request = httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "CreateUploadGrantAsync/PublicUrl need an active HTTP request to derive a base URL from.");

        return $"{request.Scheme}://{request.Host}";
    }

    /// <summary>Mirrors <c>S3MediaStorage.BuildKey</c> — same prefixes, same shape.</summary>
    private static string BuildKey(MediaPurpose purpose, string ownerId, string contentType)
    {
        var prefix = purpose switch
        {
            MediaPurpose.ListingPhoto => "listings",
            MediaPurpose.Avatar => "avatars",
            MediaPurpose.ShippingProof => "shipping",
            MediaPurpose.ReviewPhoto => "reviews",
            MediaPurpose.IdentityDocument => "identity",
            _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
        };

        var owner = EntityId.IsValid(ownerId) ? EntityId.Normalise(ownerId) : Sanitise(ownerId);

        return $"{prefix}/{owner}/{EntityId.New()}{ExtensionFor(contentType)}";
    }

    private static MediaPurpose? PurposeFromKey(string storageKey) =>
        storageKey.Split('/', 2)[0] switch
        {
            "listings" => MediaPurpose.ListingPhoto,
            "avatars" => MediaPurpose.Avatar,
            "shipping" => MediaPurpose.ShippingProof,
            "reviews" => MediaPurpose.ReviewPhoto,
            "identity" => MediaPurpose.IdentityDocument,
            _ => null,
        };

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    private static string Sanitise(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-')
            {
                buffer[length++] = c;
            }
        }

        return length == 0 ? "unknown" : new string(buffer, 0, length);
    }

    internal readonly record struct StoredObject(string ContentType, byte[] Bytes);
}

/// <summary>
/// The one HTTP door onto <see cref="LocalMediaStorage"/>'s objects: a PUT that stands in
/// for the S3 bucket a presigned grant would otherwise point at, and a GET that stands in
/// for the CDN.
/// </summary>
/// <remarks>
/// Both are <c>AllowAnonymous</c> — a visible, reviewable opt-out, same reasoning as the
/// health check. That is not a weaker rule than production's: a real presigned S3 URL is
/// exactly as anonymous, authorised only by knowing the URL. Mapped from <c>Program.cs</c>
/// only when <see cref="LocalMediaStorage"/> itself is active, so the routes do not exist
/// at all outside local development.
/// </remarks>
internal static class LocalMediaEndpoints
{
    public static IEndpointRouteBuilder MapLocalMediaEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/media/local-object").WithTags("Media (local dev)");

        group.MapPut("/{**key}", PutAsync).AllowAnonymous();
        group.MapGet("/{**key}", GetAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> PutAsync(
        string key, HttpRequest request, LocalMediaStorage storage, CancellationToken ct)
    {
        var contentType = request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || !IMediaStorage.PermittedImageTypes.Contains(contentType))
        {
            return Results.BadRequest(new { code = "media.unsupported_type" });
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);

        if (buffer.Length == 0 || buffer.Length > IMediaStorage.MaxUploadBytes)
        {
            return Results.BadRequest(new { code = "media.too_large" });
        }

        storage.Store(key, contentType, buffer.ToArray());
        return Results.Ok();
    }

    private static IResult GetAsync(string key, LocalMediaStorage storage) =>
        storage.TryGet(key, out var stored)
            ? Results.Bytes(stored.Bytes, stored.ContentType)
            : Results.NotFound();
}
#endif
