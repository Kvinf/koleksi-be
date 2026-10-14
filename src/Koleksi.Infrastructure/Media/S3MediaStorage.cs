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
    // Caching: public media keys are random and never rewritten (an edit uploads a new key), so a copy
    // can never be stale. The one-year immutable Cache-Control is added by the CloudFront response-headers
    // policy on /media/* (koleksi-infra), NOT signed into the upload: a signed Cache-Control makes the
    // browser send that header, which turns the direct-to-S3 PUT into a CORS preflight the bucket's
    // AllowedHeaders (Content-Type, Content-Length) refuses, and every photo upload then fails.

    public Task<UploadGrant> CreateUploadGrantAsync(
        MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        ValidateImageUpload(contentType, contentLength);

        var bucket = BucketFor(purpose);
        var key = BuildKeyWithExtension(purpose, ownerId, ExtensionFor(contentType));

        return Task.FromResult(SignGrant(bucket, key, contentType, contentLength));
    }

    public Task<PhotoUploadGrantSet> CreatePhotoUploadGrantSetAsync(
        string ownerId, PhotoUploadSpec original, PhotoUploadSpec large, PhotoUploadSpec small, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(large);
        ArgumentNullException.ThrowIfNull(small);

        // The two derivatives are always re-encoded as JPEG client-side (see
        // directUpload.ts) regardless of the original's own type, so only the original is
        // checked against the full allowlist here — large/small are validated as the one
        // type they are always produced as.
        ValidateImageUpload(original.ContentType, original.ContentLength);
        if (large.ContentType != "image/jpeg" || small.ContentType != "image/jpeg")
        {
            throw DomainException.For("media.unsupported_type", "Resized photo derivatives must be JPEG.");
        }
        ValidateUploadSize(large.ContentLength);
        ValidateUploadSize(small.ContentLength);

        var bucket = BucketFor(MediaPurpose.ListingPhoto);

        // No extension on the base key or the three it derives — Content-Type, signed into
        // each grant below and set on the object at PUT time, is what a browser actually
        // renders by; the key itself never needs to spell out a format.
        var key = BuildKeyWithExtension(MediaPurpose.ListingPhoto, ownerId, string.Empty);

        return Task.FromResult(new PhotoUploadGrantSet(
            StorageKey: key,
            Original: SignGrant(bucket, $"{key}/original", original.ContentType, original.ContentLength),
            Large: SignGrant(bucket, $"{key}/large", large.ContentType, large.ContentLength),
            Small: SignGrant(bucket, $"{key}/small", small.ContentType, small.ContentLength)));
    }

    private static void ValidateImageUpload(string contentType, long contentLength)
    {
        // An allowlist, checked before anything is signed. SVG is absent deliberately: it
        // is a document format that executes script, and serving one from our own origin
        // would be stored cross-site scripting against every visitor to that page.
        if (!IMediaStorage.PermittedImageTypes.Contains(contentType))
        {
            throw DomainException.For(
                "media.unsupported_type",
                "Photos must be JPEG, PNG, WebP, or AVIF.");
        }

        ValidateUploadSize(contentLength);
    }

    private static void ValidateUploadSize(long contentLength)
    {
        if (contentLength <= 0 || contentLength > IMediaStorage.MaxUploadBytes)
        {
            throw DomainException.For(
                "media.too_large",
                $"Photos must be under {IMediaStorage.MaxUploadBytes / (1024 * 1024)} MB.");
        }
    }

    internal UploadGrant SignGrant(string bucket, string key, string contentType, long contentLength)
    {
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

        var requiredHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = contentType,
            ["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture),
        };

        return new UploadGrant(
            StorageKey: key,
            UploadUrl: new Uri(url),
            RequiredHeaders: requiredHeaders,
            ExpiresAt: expiresAt);
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

        return await ObjectLooksUploadedAsync(BucketFor(purpose.Value), storageKey, ct).ConfigureAwait(false);
    }

    public async Task<bool> VerifyPhotoUploadedAsync(string storageKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        if (PurposeFromKey(storageKey) != MediaPurpose.ListingPhoto)
        {
            return false;
        }

        var bucket = BucketFor(MediaPurpose.ListingPhoto);

        // A photo stored before the three-size layout is one object, not a prefix: check that one.
        if (MediaKeys.IsLegacyListingPhotoKey(storageKey))
        {
            return await ObjectLooksUploadedAsync(bucket, storageKey, ct).ConfigureAwait(false);
        }

        // All three or none — a listing whose "large" upload silently failed would show a
        // blank card in browse forever, which is worse than refusing the whole photo now,
        // while the grant is still fresh and re-uploading costs the seller nothing.
        var original = ObjectLooksUploadedAsync(bucket, $"{storageKey}/original", ct);
        var large = ObjectLooksUploadedAsync(bucket, $"{storageKey}/large", ct);
        var small = ObjectLooksUploadedAsync(bucket, $"{storageKey}/small", ct);

        var results = await Task.WhenAll(original, large, small).ConfigureAwait(false);
        return results.All(uploaded => uploaded);
    }

    private async Task<bool> ObjectLooksUploadedAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var metadata = await s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = bucket, Key = key },
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

    public string PublicUrl(string storageKey, PhotoSize size = PhotoSize.Original)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        var purpose = PurposeFromKey(storageKey);

        // Identity documents have no public URL by construction. Reaching this with one
        // means a caller is about to publish a KTP scan, so it fails rather than returning
        // a URL that would only 403 later — and only after the key had been rendered.
        if (purpose == MediaPurpose.IdentityDocument)
        {
            throw new InvalidOperationException("Identity documents are never served publicly.");
        }

        // Every other purpose's key already names one real object; only a listing photo's
        // key is a prefix shared by three (see CreatePhotoUploadGrantSetAsync), so only it
        // needs the size folded into the path.
        // A legacy listing photo (a single object with an extension, from before the three sizes)
        // has no size folders: every size resolves to that one object rather than to a 404.
        var key = purpose == MediaPurpose.ListingPhoto && !MediaKeys.IsLegacyListingPhotoKey(storageKey)
            ? $"{storageKey}/{SizeSegment(size)}"
            : storageKey;

        return $"{options.CdnBaseUrl.TrimEnd('/')}/{key}";
    }

    private static string SizeSegment(PhotoSize size) => size switch
    {
        PhotoSize.Original => "original",
        PhotoSize.Large => "large",
        PhotoSize.Small => "small",
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

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
    /// <param name="extension">
    /// Empty for a listing photo's base key — see <see cref="CreatePhotoUploadGrantSetAsync"/>,
    /// which appends <c>/original</c>, <c>/large</c>, or <c>/small</c> to what this returns
    /// rather than relying on a file extension to tell those three apart.
    /// </param>
    private static string BuildKeyWithExtension(MediaPurpose purpose, string ownerId, string extension)
    {
        // Prefix and owner segment come from MediaKeys, the same functions the ownership check
        // (MediaKeys.IsOwnedBy) reads keys back with, so minting and checking cannot disagree.
        return $"{MediaKeys.PrefixFor(purpose)}/{MediaKeys.OwnerSegment(ownerId)}/{EntityId.New()}{extension}";
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
}
