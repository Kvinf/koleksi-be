using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// Which stored-media keys a caller may name in a request: the right purpose, their own owner
/// segment, and a key shape we minted — nothing deeper, shallower, or path-like.
/// </summary>
public sealed class MediaKeysTests
{
    private const string Owner = "b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33";
    private const string Other = "9c2e5a71-08bd-4f36-b7aa-1e4c9d05f882";

    [Theory]
    [InlineData(MediaPurpose.ListingPhoto, "listings")]
    [InlineData(MediaPurpose.Avatar, "avatars")]
    [InlineData(MediaPurpose.ShippingProof, "shipping")]
    [InlineData(MediaPurpose.ReviewPhoto, "reviews")]
    [InlineData(MediaPurpose.IdentityDocument, "identity")]
    public void A_key_in_the_callers_own_namespace_for_the_right_purpose_is_owned(MediaPurpose purpose, string prefix)
    {
        MediaKeys.PrefixFor(purpose).ShouldBe(prefix);
        MediaKeys.IsOwnedBy($"{prefix}/{Owner}/01JQ8ZC4K7N2M5V9XR3P6TW0YB.jpg", purpose, Owner).ShouldBeTrue();
    }

    [Theory]
    [InlineData(MediaPurpose.ListingPhoto, "avatars/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    [InlineData(MediaPurpose.Avatar, "listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    [InlineData(MediaPurpose.ShippingProof, "reviews/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    [InlineData(MediaPurpose.IdentityDocument, "avatars/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    public void A_key_of_another_purpose_is_not_owned(MediaPurpose purpose, string key) =>
        MediaKeys.IsOwnedBy(key, purpose, Owner).ShouldBeFalse();

    [Fact]
    public void Another_accounts_key_is_not_owned_whatever_its_purpose()
    {
        foreach (var purpose in Enum.GetValues<MediaPurpose>())
        {
            MediaKeys.IsOwnedBy($"{MediaKeys.PrefixFor(purpose)}/{Other}/x", purpose, Owner).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("listings")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x/original")]
    [InlineData("/listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    [InlineData("listings//x")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/..")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/.")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/a b")]
    [InlineData("listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/a%2Fb")]
    [InlineData("listings\\b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33\\x")]
    [InlineData("listings/../listings/b41f7c2e-3a9d-4c11-9f0e-5d8a1b6c2d33/x")]
    public void A_key_that_is_not_the_exact_shape_we_mint_is_not_owned(string? key) =>
        MediaKeys.IsOwnedBy(key, MediaPurpose.ListingPhoto, Owner).ShouldBeFalse();

    [Fact]
    public void An_overlong_key_is_not_owned()
    {
        MediaKeys.IsOwnedBy($"listings/{Owner}/{new string('a', 300)}", MediaPurpose.ListingPhoto, Owner).ShouldBeFalse();
    }

    [Fact]
    public void The_owner_segment_is_the_same_one_the_grant_mints()
    {
        MediaKeys.OwnerSegment(Owner).ShouldBe(Owner);

        // An id shaped like an entity id is normalised to its canonical (upper-case) form.
        MediaKeys.OwnerSegment("01jq8zc4k7n2m5v9xr3p6tw0yb").ShouldBe("01JQ8ZC4K7N2M5V9XR3P6TW0YB");

        // Anything outside letters, digits and hyphen is dropped; nothing path-like survives.
        MediaKeys.OwnerSegment("a/b..c d").ShouldBe("abcd");
        MediaKeys.OwnerSegment("///").ShouldBe("unknown");
    }

    // ─── The combined check ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_key_that_is_not_owned_never_reaches_storage_and_answers_like_a_missing_upload()
    {
        var storage = new CountingMedia { Exists = true };

        (await storage.VerifyOwnedUploadAsync(
            MediaPurpose.ShippingProof, Owner, $"shipping/{Other}/x", CancellationToken.None)).ShouldBeFalse();

        storage.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task An_owned_key_is_confirmed_against_storage_by_the_right_check()
    {
        var storage = new CountingMedia { Exists = true };

        (await storage.VerifyOwnedUploadAsync(
            MediaPurpose.Avatar, Owner, $"avatars/{Owner}/me.jpg", CancellationToken.None)).ShouldBeTrue();
        storage.SingleObjectChecks.ShouldBe(1);
        storage.PhotoSetChecks.ShouldBe(0);

        (await storage.VerifyOwnedUploadAsync(
            MediaPurpose.ListingPhoto, Owner, $"listings/{Owner}/p", CancellationToken.None)).ShouldBeTrue();
        storage.PhotoSetChecks.ShouldBe(1);
    }

    [Fact]
    public async Task An_owned_key_that_was_never_uploaded_is_refused()
    {
        var storage = new CountingMedia { Exists = false };

        (await storage.VerifyOwnedUploadAsync(
            MediaPurpose.IdentityDocument, Owner, $"identity/{Owner}/ktp.jpg", CancellationToken.None)).ShouldBeFalse();
        storage.Calls.ShouldBe(1);
    }

    private sealed class CountingMedia : IMediaStorage
    {
        public bool Exists { get; init; }

        public int SingleObjectChecks { get; private set; }

        public int PhotoSetChecks { get; private set; }

        public int Calls => SingleObjectChecks + PhotoSetChecks;

        public Task<bool> VerifyUploadedAsync(string storageKey, CancellationToken ct)
        {
            SingleObjectChecks++;
            return Task.FromResult(Exists);
        }

        public Task<bool> VerifyPhotoUploadedAsync(string storageKey, CancellationToken ct)
        {
            PhotoSetChecks++;
            return Task.FromResult(Exists);
        }

        public Task<UploadGrant> CreateUploadGrantAsync(
            MediaPurpose purpose, string ownerId, string contentType, long contentLength, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PhotoUploadGrantSet> CreatePhotoUploadGrantSetAsync(
            string ownerId, PhotoUploadSpec original, PhotoUploadSpec large, PhotoUploadSpec small, CancellationToken ct) =>
            throw new NotSupportedException();

        public string PublicUrl(string storageKey, PhotoSize size = PhotoSize.Original) =>
            throw new NotSupportedException();
    }
}
