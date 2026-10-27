using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// Issues presigned upload grants for user-supplied media: avatars, identity documents,
/// and anything else a signed-in caller needs to put an image in front of us.
/// </summary>
/// <remarks>
/// The one HTTP door onto <see cref="IMediaStorage.CreateUploadGrantAsync"/>. The
/// interface has always constrained what a grant can be redeemed for — the content type,
/// the size, the key — this endpoint only adds who may ask: any signed-in caller, for
/// their own <see cref="ICurrentUser.RequireUserId"/>, never a caller-supplied owner.
/// </remarks>
internal static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/media").WithTags("Media");

        group.MapPost("/upload-grant", CreateUploadGrantAsync).WithName("CreateUploadGrant");
        group.MapPost("/photo-upload-grants", CreatePhotoUploadGrantsAsync).WithName("CreatePhotoUploadGrants");

        return app;
    }

    /// <summary>
    /// Grants a listing photo's three uploads (original, large, small) in one call — see
    /// <see cref="IMediaStorage.CreatePhotoUploadGrantSetAsync"/>. The only media purpose
    /// this covers; avatars, review photos, shipping proof, and identity documents stay on
    /// the single-grant endpoint above, since none of them render at more than one size.
    /// </summary>
    private static async Task<IResult> CreatePhotoUploadGrantsAsync(
        PhotoUploadGrantsRequest request, ICurrentUser user, IMediaStorage media, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.OriginalContentType)
            || request.OriginalContentLength <= 0
            || request.LargeContentLength <= 0
            || request.SmallContentLength <= 0)
        {
            return Results.BadRequest(new { code = "media.invalid_request" });
        }

        var grants = await media.CreatePhotoUploadGrantSetAsync(
            user.RequireUserId(),
            new PhotoUploadSpec(request.OriginalContentType, request.OriginalContentLength),

            // The two derivatives are always re-encoded as JPEG client-side — see
            // directUpload.ts — so their content type is not part of the request at all.
            new PhotoUploadSpec("image/jpeg", request.LargeContentLength),
            new PhotoUploadSpec("image/jpeg", request.SmallContentLength),
            ct).ConfigureAwait(false);

        static object ToResponse(UploadGrant grant) => new
        {
            grant.StorageKey,
            UploadUrl = grant.UploadUrl.ToString(),
            grant.RequiredHeaders,
        };

        return Results.Ok(new
        {
            grants.StorageKey,
            Original = ToResponse(grants.Original),
            Large = ToResponse(grants.Large),
            Small = ToResponse(grants.Small),
            grants.Original.ExpiresAt,
        });
    }

    private sealed record PhotoUploadGrantsRequest(
        string OriginalContentType, long OriginalContentLength, long LargeContentLength, long SmallContentLength);

    private static async Task<IResult> CreateUploadGrantAsync(
        UploadGrantRequest request, ICurrentUser user, IMediaStorage media, FeaturesOptions features, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Nothing reviews identity documents yet, so none is accepted (see FeaturesOptions.IdVerificationEnabled).
        if (request.Purpose == MediaPurpose.IdentityDocument && !features.IdVerificationEnabled)
        {
            return Results.NotFound(new { code = "feature.unavailable" });
        }

        if (string.IsNullOrWhiteSpace(request.ContentType) || request.ContentLength <= 0)
        {
            return Results.BadRequest(new { code = "media.invalid_request" });
        }

        // Type/size/purpose validation, and the DomainException that follows from any of
        // them being wrong, live in IMediaStorage — see S3MediaStorage.CreateUploadGrantAsync.
        // This endpoint's only job is to supply an owner the caller cannot forge.
        var grant = await media
            .CreateUploadGrantAsync(request.Purpose, user.RequireUserId(), request.ContentType, request.ContentLength, ct)
            .ConfigureAwait(false);

        return Results.Ok(new
        {
            grant.StorageKey,
            UploadUrl = grant.UploadUrl.ToString(),
            grant.RequiredHeaders,
            grant.ExpiresAt,
        });
    }

    private sealed record UploadGrantRequest(MediaPurpose Purpose, string ContentType, long ContentLength);
}
