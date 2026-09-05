namespace Koleksi.Domain.Catalog;

/// <summary>
/// One photo attached to a listing, referenced by its object key in our own media
/// bucket.
/// </summary>
/// <param name="StorageKey">
/// The S3 object key, e.g. <c>listings/01J8Z.../0.jpg</c>. Always a key, never a URL.
/// </param>
/// <param name="Width">Pixel width, recorded at upload so the frontend can reserve layout space.</param>
/// <param name="Height">Pixel height.</param>
/// <remarks>
/// <para>
/// Storing a key rather than a URL is a security decision, not a tidiness one. If the
/// domain held arbitrary user-supplied URLs, a seller could point a listing photo at
/// any host — turning our pages into a vector for tracking pixels, mixed content, or
/// phishing imagery served from a domain that looks like ours. Keys are minted by our
/// own presigned-upload endpoint, so the origin of every byte we render is known.
/// </para>
/// <para>
/// The public URL is composed at the read boundary from the CDN base plus the key.
/// </para>
/// </remarks>
public readonly record struct ListingPhoto(string StorageKey, int Width, int Height)
{
    /// <summary>
    /// Object keys the platform mints, guarding against a caller passing a traversal
    /// path or an absolute URL where a key is expected.
    /// </summary>
    public static ListingPhoto Create(string storageKey, int width, int height)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        if (storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.StartsWith('/')
            || storageKey.Contains("://", StringComparison.Ordinal))
        {
            throw new ArgumentException("Photo storage key must be a relative object key.", nameof(storageKey));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        return new ListingPhoto(storageKey, width, height);
    }

    /// <summary>Aspect ratio, used to pick between the 1:1 grid tile and the 4:3 detail frame.</summary>
    public double AspectRatio => (double)Width / Height;
}
