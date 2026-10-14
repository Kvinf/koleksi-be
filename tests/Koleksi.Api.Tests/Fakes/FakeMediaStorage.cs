using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IMediaStorage"/>.
/// </summary>
/// <remarks>
/// The API's contract with this adapter is that a stored key becomes a URL on a base we
/// own. The rules that make that safe — the content-type allowlist, the size cap, and
/// the refusal to publish an identity document — live in the S3 implementation and are
/// tested there, against no network. This fake exists only so the item endpoint has
/// something to render photos with.
/// </remarks>
public sealed class FakeMediaStorage : FakeAdapter, IMediaStorage
{
    public const string CdnBase = "https://media.koleksi.test";

    /// <summary>What the next <see cref="VerifyUploadedAsync"/> call returns. Defaults to success.</summary>
    public bool NextVerifyUploadedResult { get; set; } = true;

    public Task<UploadGrant> CreateUploadGrantAsync(
        MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct)
    {
        ThrowIfFailing();

        var key = $"{purpose}/{ownerId}/test-object";
        return Task.FromResult(new UploadGrant(
            key,
            new Uri($"{CdnBase}/upload/{key}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = contentType },
            DateTimeOffset.UnixEpoch.AddYears(60)));
    }

    public Task<PhotoUploadGrantSet> CreatePhotoUploadGrantSetAsync(
        string ownerId, PhotoUploadSpec original, PhotoUploadSpec large, PhotoUploadSpec small, CancellationToken ct)
    {
        ThrowIfFailing();

        var key = $"listings/{ownerId}/test-object";
        UploadGrant Grant(string suffix, string contentType) => new(
            $"{key}/{suffix}",
            new Uri($"{CdnBase}/upload/{key}/{suffix}"),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = contentType },
            DateTimeOffset.UnixEpoch.AddYears(60));

        return Task.FromResult(new PhotoUploadGrantSet(
            key,
            Grant("original", original.ContentType),
            Grant("large", large.ContentType),
            Grant("small", small.ContentType)));
    }

    public Task<bool> VerifyUploadedAsync(string storageKey, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextVerifyUploadedResult);
    }

    public Task<bool> VerifyPhotoUploadedAsync(string storageKey, CancellationToken ct)
    {
        ThrowIfFailing();
        return Task.FromResult(NextVerifyUploadedResult);
    }

    public string PublicUrl(string storageKey, PhotoSize size = PhotoSize.Original)
    {
        ThrowIfFailing();
        return $"{CdnBase}/{storageKey}";
    }
}
