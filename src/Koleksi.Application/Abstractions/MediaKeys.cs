using Koleksi.Domain.Common;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// The shape of a stored-media key, and the one check every endpoint that accepts a key from a
/// client runs before attaching it to anything.
/// </summary>
/// <remarks>
/// <para>
/// A key arrives from the browser inside a request body, so it is just a string the caller chose.
/// A grant is bound to the account that asked for it, but a request that merely <i>names</i> a
/// key is not: without a check here, a caller could attach another account's uploaded photo
/// (or its private identity document) to their own listing, profile or shipment, or point a
/// shipping proof at an avatar. <see cref="VerifyOwnedUploadAsync"/> is that check: the key is
/// the right purpose, its owner segment is the authenticated caller, and the object exists.
/// </para>
/// <para>
/// The layout is the one <c>IMediaStorage.CreateUploadGrantAsync</c> mints:
/// <c>{purpose prefix}/{owner}/{random id}</c>, where a listing photo's key is a prefix shared by
/// its three size objects. The owner and prefix rules live here once and the S3 adapter builds its
/// keys with the same functions, so the two cannot drift apart.
/// </para>
/// </remarks>
public static class MediaKeys
{
    private const int MaxKeyLength = 200;

    /// <summary>The first path segment of every key minted for <paramref name="purpose"/>.</summary>
    public static string PrefixFor(MediaPurpose purpose) => purpose switch
    {
        MediaPurpose.ListingPhoto => "listings",
        MediaPurpose.Avatar => "avatars",
        MediaPurpose.ShippingProof => "shipping",
        MediaPurpose.ReviewPhoto => "reviews",
        MediaPurpose.IdentityDocument => "identity",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    /// <summary>
    /// The owner as it appears inside a key. The owner id is a Cognito subject (a UUID) with
    /// nothing that needs escaping, but it is still normalised rather than trusted verbatim.
    /// </summary>
    public static string OwnerSegment(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return EntityId.IsValid(ownerId) ? EntityId.Normalise(ownerId) : Sanitise(ownerId);
    }

    /// <summary>
    /// Whether <paramref name="storageKey"/> is a well-formed key of this purpose that belongs to
    /// <paramref name="ownerId"/>. Says nothing about whether anything was uploaded.
    /// </summary>
    public static bool IsOwnedBy(string? storageKey, MediaPurpose purpose, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || storageKey.Length > MaxKeyLength || string.IsNullOrWhiteSpace(ownerId))
        {
            return false;
        }

        // Exactly prefix/owner/id. Anything deeper (a listing photo's size suffix is added by the
        // storage adapter, never sent by a client) or shallower is not a key we minted.
        var segments = storageKey.Split('/');
        return segments.Length == 3
            && string.Equals(segments[0], PrefixFor(purpose), StringComparison.Ordinal)
            && string.Equals(segments[1], OwnerSegment(ownerId), StringComparison.Ordinal)
            && IsPlainSegment(segments[2]);
    }

    /// <summary>
    /// The single check for a client-supplied media key: right purpose, owned by the caller, and
    /// actually uploaded. A key that is someone else's answers exactly like one that was never
    /// uploaded, so the answer cannot be used to probe which keys exist.
    /// </summary>
    public static async Task<bool> VerifyOwnedUploadAsync(
        this IMediaStorage media, MediaPurpose purpose, string ownerId, string? storageKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (!IsOwnedBy(storageKey, purpose, ownerId))
        {
            return false;
        }

        return purpose == MediaPurpose.ListingPhoto
            ? await media.VerifyPhotoUploadedAsync(storageKey!, ct).ConfigureAwait(false)
            : await media.VerifyUploadedAsync(storageKey!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a listing-photo key predates the three-size layout. Those keys were minted with a
    /// file extension (<c>listings/{owner}/{id}.jpg</c>) and name one object; current keys have no
    /// extension and are a prefix shared by <c>original</c>, <c>large</c> and <c>small</c>.
    /// </summary>
    /// <remarks>
    /// Everything that turns a listing-photo key into an object (the public URL, the upload check)
    /// must ask this first, or a legacy photo is looked up under folders that were never written.
    /// </remarks>
    public static bool IsLegacyListingPhotoKey(string storageKey)
    {
        ArgumentNullException.ThrowIfNull(storageKey);

        var lastSlash = storageKey.LastIndexOf('/');
        return storageKey.StartsWith(PrefixFor(MediaPurpose.ListingPhoto) + "/", StringComparison.Ordinal)
            && storageKey.IndexOf('.', lastSlash + 1) >= 0;
    }

    private static bool IsPlainSegment(string segment) =>
        segment.Length > 0
        && segment is not ("." or "..")
        && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

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
