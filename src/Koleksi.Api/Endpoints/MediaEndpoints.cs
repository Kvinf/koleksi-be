using Koleksi.Application.Abstractions;

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

        return app;
    }

    private static async Task<IResult> CreateUploadGrantAsync(
        UploadGrantRequest request, ICurrentUser user, IMediaStorage media, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

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
