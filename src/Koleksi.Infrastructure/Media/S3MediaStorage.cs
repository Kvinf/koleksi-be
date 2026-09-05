using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Infrastructure.Media;

/// <summary>
/// Issues presigned S3 uploads and verifies what actually landed.
/// </summary>
/// <remarks>
/// <para>
/// Bytes never pass through the API. The client asks for a grant, PUTs straight to S3,
/// and returns only the object key. Streaming multi-megabyte images through Lambda would
/// be slow and expensive, and would put attacker-controlled bytes inside our execution
/// environment for no benefit.
/// </para>
/// <para>
/// The grant is what makes that safe, and it is safe only because the server decides
/// every part of it. The client chooses none of the key, and the signature covers both
/// the content type and the content length — so a grant for a 2 MB JPEG cannot be
/// redeemed for a 200 MB anything, and cannot be pointed at another user's prefix.
/// </para>
/// </remarks>
internal sealed class S3MediaStorage(IAmazonS3 s3, MediaOptions options) : IMediaStorage
{
    public Task<UploadGrant> CreateUploadGrantAsync(
        MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        // An allowlist, checked before anything is signed. SVG is absent deliberately: it
        // is a document format that executes script, and serving one from our own origin
        // would be stored cross-site scripting against every visitor to that page.
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

        var bucket = BucketFor(purpose);
        var key = BuildKey(purpose, ownerId, contentType);
        var expiresAt = DateTimeOffset.UtcNow + options.UploadGrantLifetime;

        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,

            // Signing the content type pins what the object will later be served as. Left
            // unsigned, a client could take a grant for a JPEG and upload text/html, and
            // the bucket would faithfully serve it back as a page on our own origin.
            ContentType = contentType,
        };

        // The SDK's presign path does not infer scheme from AmazonS3Config.ServiceURL — left
        // alone it signs an https:// URL even when talking to a plain-http local endpoint,
        // and a browser PUT to that URL then fails the TLS handshake before the signature is
        // ever checked. Unset (the deployed case) it defaults to https, which is what real S3
        // always wants.
        if (Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var localEndpoint))
        {
            request.Protocol = localEndpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
        }

        // Signing the length too, so the grant cannot be redeemed for an arbitrarily large
        // object. Without it the size check above only constrains what the client claims.
        request.Headers["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture);

        var url = s3.GetPreSignedURL(request);

        return Task.FromResult(new UploadGrant(
            StorageKey: key,
            UploadUrl: new Uri(url),
            RequiredHeaders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = contentType,
                ["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture),
            },
            ExpiresAt: expiresAt));
    }

    public async Task<bool> VerifyUploadedAsync(string storageKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        // The grant constrains the request; this checks the result. A client that ignored
        // the signed headers, or uploaded nothing at all, must not end up with that key
        // attached to a listing.
        var purpose = PurposeFromKey(storageKey);
        if (purpose is null)
        {
            return false;
        }

        try
        {
            var metadata = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = BucketFor(purpose.Value), Key = storageKey },
                ct).ConfigureAwait(false);

            if (metadata.ContentLength <= 0 || metadata.ContentLength > IMediaStorage.MaxUploadBytes)
            {
                return false;
            }

            // S3 records the Content-Type the client sent. The presigned signature already
            // constrained it, so this is a second, independent check rather than the only
            // one — the two would have to fail together for a bad type to get through.
            return metadata.Headers.ContentType is { } stored
                && IMediaStorage.PermittedImageTypes.Contains(stored);
        }
        catch (AmazonS3Exception failure) when (failure.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // The grant was never redeemed.
            return false;
        }
    }

    public string PublicUrl(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        // Identity documents have no public URL by construction. Reaching this with one
        // means a caller is about to publish a KTP scan, so it fails rather than returning
        // a URL that would only 403 later — and only after the key had been rendered.
        if (PurposeFromKey(storageKey) == MediaPurpose.IdentityDocument)
        {
            throw new InvalidOperationException("Identity documents are never served publicly.");
        }

        return $"{options.CdnBaseUrl.TrimEnd('/')}/{storageKey}";
    }

    private string BucketFor(MediaPurpose purpose) => purpose == MediaPurpose.IdentityDocument
        ? options.PrivateBucket
        : options.PublicBucket;

    /// <summary>
    /// Mints the object key.
    /// </summary>
    /// <remarks>
    /// Entirely server-chosen. If the client supplied any part of it, a key of
    /// <c>../../someone-else/avatar.jpg</c> — or simply another user's prefix — would be a
    /// grant to overwrite someone else's media. The random id also means keys are not
    /// enumerable, so the private bucket's contents cannot be guessed.
    /// </remarks>
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

        // The owner id is a Cognito subject — a UUID, with no characters that need
        // escaping in a key — but it is still normalised rather than trusted verbatim.
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

        // Unreachable: the allowlist is checked before this is called. Present so that
        // adding a type to the allowlist and forgetting this map fails loudly.
        _ => throw new ArgumentOutOfRangeException(nameof(contentType)),
    };

    /// <summary>Reduces a value to characters that are unambiguous in an object key.</summary>
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
}
