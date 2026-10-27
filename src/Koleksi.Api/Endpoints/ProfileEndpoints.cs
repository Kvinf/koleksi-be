using Koleksi.Application.Abstractions;
using Koleksi.Infrastructure;
using Koleksi.Application.Features.Orders;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Common;
using Koleksi.Domain.Users;

using Microsoft.AspNetCore.RateLimiting;

using static Koleksi.Api.Endpoints.IdempotencyHeader;

namespace Koleksi.Api.Endpoints;

/// <summary>
/// The signed-in account's profile: identity, shipping addresses, phone verification, the
/// payout bank account, and identity-document verification.
/// </summary>
/// <remarks>
/// <para>
/// Every route here reads the caller's id from <see cref="ICurrentUser"/>, never from the
/// body or a route parameter — there is no "whose profile" question to answer, because the
/// group carries no <c>AllowAnonymous</c> and the fallback policy already requires a
/// signed-in caller, so this is always and only the caller's own profile.
/// </para>
/// <para>
/// A missing profile is not a <c>404</c>. An account that has never saved one is a normal,
/// common state — every fresh sign-up starts there — so <c>GET</c> answers with an empty
/// profile rather than an error, exactly as the frontend's placeholder state already
/// assumed before this endpoint existed.
/// </para>
/// </remarks>
internal static class ProfileEndpoints
{
    /// <summary>Longest phone number accepted. A real one is 8 to 15 digits; this leaves room for spacing.</summary>
    private const int MaxPhoneLength = 20;

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/profile").WithTags("Profile");

        group.MapGet("", GetProfileAsync).WithName("GetProfile");
        group.MapPut("", SaveProfileAsync).WithName("SaveProfile");
        group.MapPost("/avatar", SetAvatarAsync).WithName("SetProfileAvatar");
        group.MapPost("/bank-account", SetBankAccountAsync).WithName("SetProfileBankAccount");
        group.MapPost("/id-verification", SubmitIdVerificationAsync).WithName("SubmitIdVerification");
        group.MapPost("/phone/otp", RequestPhoneOtpAsync)
            .RequireRateLimiting(RateLimitPolicies.VerificationCode)
            .WithName("RequestProfilePhoneOtp");
        group.MapPost("/phone/otp/verify", VerifyPhoneOtpAsync).WithName("VerifyProfilePhoneOtp");

        return app;
    }

    private static async Task<IResult> GetProfileAsync(
        ICurrentUser user,
        IProfileRepository repository,
        IMediaStorage media,
        AccountStandingService standing,
        CancellationToken ct)
    {
        var userId = user.RequireUserId();
        var profile = await repository.FindAsync(userId, ct).ConfigureAwait(false);

        // How many warnings the account carries, and whether it is banned. Only on this read: the saved
        // responses (which the idempotency store replays) never carry it, so a stale copy cannot be served.
        return Results.Ok(ToResponse(profile, media) with
        {
            Standing = await standing.GetAsync(userId, ct).ConfigureAwait(false),
        });
    }

    private static async Task<IResult> SaveProfileAsync(
        SaveProfileRequest request,
        ICurrentUser user,
        IProfileRepository repository,
        IMediaStorage media,
        IIdempotencyStore idempotency,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return Results.BadRequest(new { code = "profile.name_required" });
        }

        // A body that leaves the list out (or sends null, or a null row) is a malformed request,
        // not a server fault: without this the Select below threw a NullReferenceException (500).
        if (request.Addresses is null || request.Addresses.Any(a => a is null))
        {
            return Results.BadRequest(new { code = "profile.addresses_invalid" });
        }

        var response = await idempotency.RunOnceAsync(
            $"profile.update:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            async () =>
            {
                var userId = user.RequireUserId();
                var existing = await repository.FindAsync(userId, ct).ConfigureAwait(false);

                // A new address must carry a province. One stored before provinces existed may be
                // re-saved without it (matched by its own id), so editing the name or another
                // address does not fail on a row the owner has not touched.
                var legacyIds = existing is null
                    ? []
                    : existing.Addresses
                        .Where(a => string.IsNullOrWhiteSpace(a.Province))
                        .Select(a => a.Id)
                        .ToHashSet(StringComparer.Ordinal);

                var addresses = request.Addresses
                    .Select(a => Address.Create(
                        a.Id, a.Label, a.RecipientName, a.Phone, a.Line, a.City, a.Province, a.PostalCode, a.IsDefault,
                        provinceOptional: a.Id is not null && legacyIds.Contains(a.Id)))
                    .ToList();

                if (existing is null)
                {
                    var created = UserProfile.CreateEmpty(userId, clock);
                    created.UpdateDetails(request.FullName, addresses, clock);
                    await repository.AddAsync(created, ct).ConfigureAwait(false);
                    return ToResponse(created, media);
                }

                var expectedVersion = existing.Version;
                existing.UpdateDetails(request.FullName, addresses, clock);
                await repository.UpdateAsync(existing, expectedVersion, ct).ConfigureAwait(false);
                return ToResponse(existing, media);
            },
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> SetAvatarAsync(
        SetAvatarRequest request,
        ICurrentUser user,
        IProfileRepository repository,
        IMediaStorage media,
        IIdempotencyStore idempotency,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AvatarKey))
        {
            return Results.BadRequest(new { code = "profile.avatar_required" });
        }

        // The grant constrained the upload; this confirms something was actually put there
        // before it is attached to the account, and that it is the caller's own avatar upload
        // rather than someone else's photo (or any other purpose's object) named by key.
        if (!await media.VerifyOwnedUploadAsync(MediaPurpose.Avatar, user.RequireUserId(), request.AvatarKey, ct)
            .ConfigureAwait(false))
        {
            return Results.BadRequest(new { code = "profile.avatar_not_uploaded" });
        }

        var response = await idempotency.RunOnceAsync(
            $"profile.avatar:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => WithProfileAsync(
                user, repository, clock,
                (profile, clk) => profile.SetAvatar(request.AvatarKey, clk),
                media, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> SetBankAccountAsync(
        SetBankAccountRequest request,
        ICurrentUser user,
        IProfileRepository repository,
        IMediaStorage media,
        PayoutSender payoutSender,
        IIdempotencyStore idempotency,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.BankName)
            || string.IsNullOrWhiteSpace(request.AccountNumber)
            || string.IsNullOrWhiteSpace(request.AccountHolderName))
        {
            return Results.BadRequest(new { code = "profile.bank_account_invalid" });
        }

        var account = BankAccount.Create(request.BankName, request.AccountNumber, request.AccountHolderName);

        var response = await idempotency.RunOnceAsync(
            $"profile.bank-account:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => WithProfileAsync(
                user, repository, clock,
                (profile, clk) => profile.SetBankAccount(account, clk),
                media, ct),
            ct).ConfigureAwait(false);

        // Money that was waiting for exactly this goes out now instead of on the next backoff.
        await payoutSender.RetryWaitingForBankAccountAsync(user.RequireUserId(), ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    private static async Task<IResult> SubmitIdVerificationAsync(
        SubmitIdVerificationRequest request,
        ICurrentUser user,
        IProfileRepository repository,
        IMediaStorage media,
        IIdempotencyStore idempotency,
        IClock clock,
        FeaturesOptions features,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!features.IdVerificationEnabled)
        {
            return Results.NotFound(new { code = "feature.unavailable" });
        }

        if (string.IsNullOrWhiteSpace(request.IdPhotoKey))
        {
            return Results.BadRequest(new { code = "profile.id_photos_required" });
        }

        // Must be the caller's own identity-document upload (private bucket), not another
        // account's document and not a public photo. The key is then kept on the profile.
        if (!await media.VerifyOwnedUploadAsync(MediaPurpose.IdentityDocument, user.RequireUserId(), request.IdPhotoKey, ct)
            .ConfigureAwait(false))
        {
            return Results.BadRequest(new { code = "profile.id_photos_not_uploaded" });
        }

        var response = await idempotency.RunOnceAsync(
            $"profile.id-verification:{user.RequireUserId()}",
            Read(http),
            TimeSpan.FromMinutes(2),
            () => WithProfileAsync(
                user, repository, clock,
                (profile, clk) => profile.SubmitIdVerification(request.IdPhotoKey, clk),
                media, ct),
            ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    /// <summary>
    /// Requests a verification code. To the phone, when the reader has already entered one
    /// — otherwise to the account's own email, resolved from the caller's own bearer token,
    /// so a reader who has not typed a number in yet is never blocked from verifying at all.
    /// </summary>
    /// <remarks>
    /// Every request sends a real SMS or email, so it is limited three ways: the per-user
    /// <see cref="RateLimitPolicies.VerificationCode"/> bucket in front, then, server-side, a
    /// cooldown between codes to the same destination and a cap per account per hour
    /// (<see cref="IVerificationThrottle"/>). The throttle runs inside the idempotent work, so a
    /// double-tap carrying one key replays the first answer instead of tripping the cooldown.
    /// </remarks>
    private static async Task<IResult> RequestPhoneOtpAsync(
        RequestPhoneOtpRequest request,
        ICurrentUser user,
        IIdentityService identity,
        IPhoneVerificationService phoneVerification,
        IEmailVerificationService emailVerification,
        IVerificationThrottle throttle,
        IIdempotencyStore idempotency,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var phone = request.Phone?.Trim();
        var viaEmail = string.IsNullOrWhiteSpace(phone);

        // A phone number is a handful of digits; anything longer is not one, and would otherwise
        // flow into an SMS destination and a storage key as sent.
        if (!viaEmail && phone!.Length > MaxPhoneLength)
        {
            return Results.BadRequest(new { code = "profile.phone_invalid" });
        }

        string? email = null;
        if (viaEmail)
        {
            var bearerToken = BearerToken.Read(http);
            email = bearerToken is null ? null : await identity.GetEmailAsync(bearerToken, ct).ConfigureAwait(false);

            if (email is null)
            {
                // Can't happen for a real, authenticated caller — the fallback policy
                // already required a valid token to reach here — but a coarse refusal
                // costs nothing and this endpoint has no more specific answer to give.
                return Results.BadRequest(new { code = "profile.phone_invalid" });
            }
        }

        var userId = user.RequireUserId();
        var destination = viaEmail ? email! : phone!;

        // Short retention, same reasoning as the auth email-OTP request: long enough to
        // absorb a double-tap on "Send code", short enough that a genuine second request a
        // minute later still gets a fresh one.
        var devCode = await idempotency.RunOnceAsync(
            viaEmail ? $"profile.email-otp-request:{userId}" : $"profile.phone-otp-request:{userId}",
            Read(http),
            TimeSpan.FromSeconds(30),
            async () =>
            {
                switch (await throttle.TryAcquireAsync(userId, destination, ct).ConfigureAwait(false))
                {
                    case VerificationThrottleResult.Cooldown:
                        throw DomainException.For(
                            "profile.otp_cooldown", "A code was just sent. Wait a minute before asking for another.");

                    case VerificationThrottleResult.HourlyLimit:
                        throw DomainException.For(
                            "profile.otp_limit", "Too many codes requested. Try again later.");
                }

                return viaEmail
                    ? await emailVerification.RequestCodeAsync(userId, email!, ct).ConfigureAwait(false)
                    : await phoneVerification.RequestCodeAsync(userId, phone!, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        // devCode is null in production — see IPhoneVerificationService.RequestCodeAsync's
        // and IEmailVerificationService.RequestCodeAsync's doc comments. Present here for
        // the same reason /api/auth/otp/request carries one: local development has no SMS
        // gateway or SES identity to read the code from otherwise.
        return Results.Ok(new { DevCode = devCode, Channel = viaEmail ? "email" : "sms", Email = email });
    }

    private static async Task<IResult> VerifyPhoneOtpAsync(
        VerifyPhoneOtpRequest request,
        ICurrentUser user,
        IIdentityService identity,
        IProfileRepository repository,
        IMediaStorage media,
        IPhoneVerificationService phoneVerification,
        IEmailVerificationService emailVerification,
        IClock clock,
        HttpRequest http,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(new { code = "profile.code_invalid" });
        }

        var phone = request.Phone?.Trim();
        var code = request.Code.Trim();

        // Neither is a value a real client sends; bounded before it reaches a storage key.
        if (code.Length > 12 || phone is { Length: > MaxPhoneLength })
        {
            return Results.BadRequest(new { code = "profile.code_invalid" });
        }

        bool verified;

        if (string.IsNullOrWhiteSpace(phone))
        {
            var bearerToken = BearerToken.Read(http);
            var email = bearerToken is null ? null : await identity.GetEmailAsync(bearerToken, ct).ConfigureAwait(false);

            verified = email is not null
                && await emailVerification.VerifyCodeAsync(user.RequireUserId(), email, code, ct).ConfigureAwait(false);
        }
        else
        {
            verified = await phoneVerification.VerifyCodeAsync(user.RequireUserId(), phone, code, ct).ConfigureAwait(false);
        }

        // Deliberately not idempotency-wrapped — same reasoning as /api/auth/otp/verify: the
        // code is already single-use, so a genuine replay is naturally answered "no" again.
        if (!verified)
        {
            return Results.BadRequest(new { code = "profile.code_invalid" });
        }

        var response = await WithProfileAsync(
            user, repository, clock,
            (profile, clk) => profile.MarkPhoneVerified(string.IsNullOrWhiteSpace(phone) ? null : phone, clk),
            media, ct).ConfigureAwait(false);

        return Results.Ok(response);
    }

    /// <summary>
    /// Loads the caller's profile (creating an empty one if this is their first save),
    /// applies one mutation, and persists it.
    /// </summary>
    private static async Task<ProfileResponse> WithProfileAsync(
        ICurrentUser user,
        IProfileRepository repository,
        IClock clock,
        Action<UserProfile, IClock> mutate,
        IMediaStorage media,
        CancellationToken ct)
    {
        var userId = user.RequireUserId();
        var existing = await repository.FindAsync(userId, ct).ConfigureAwait(false);

        if (existing is null)
        {
            var created = UserProfile.CreateEmpty(userId, clock);
            mutate(created, clock);
            await repository.AddAsync(created, ct).ConfigureAwait(false);
            return ToResponse(created, media);
        }

        var expectedVersion = existing.Version;
        mutate(existing, clock);
        await repository.UpdateAsync(existing, expectedVersion, ct).ConfigureAwait(false);
        return ToResponse(existing, media);
    }

    private static ProfileResponse ToResponse(UserProfile? profile, IMediaStorage media)
    {
        if (profile is null)
        {
            return new ProfileResponse(
                string.Empty, null, false, null, nameof(IdVerificationStatus.None), null, []);
        }

        return new ProfileResponse(
            profile.FullName,
            profile.Phone,
            profile.PhoneVerified,
            profile.AvatarKey is { } key ? media.PublicUrl(key) : null,
            profile.IdVerificationStatus.ToString(),
            profile.BankAccount is { } bank
                ? new BankAccountResponse(bank.BankName, bank.Masked, bank.AccountHolderName)
                : null,
            profile.Addresses
                .Select(a => new AddressResponse(
                    a.Id, a.Label, a.RecipientName, a.Phone, a.Line, a.City, a.Province, a.PostalCode, a.IsDefault))
                .ToList());
    }

    private sealed record AddressRequest(
        string? Id,
        string Label,
        string RecipientName,
        string Phone,
        string Line,
        string City,
        string? Province,
        string PostalCode,
        bool IsDefault);

    private sealed record SaveProfileRequest(string FullName, IReadOnlyList<AddressRequest> Addresses);

    private sealed record SetAvatarRequest(string AvatarKey);

    private sealed record SetBankAccountRequest(string BankName, string AccountNumber, string AccountHolderName);

    private sealed record SubmitIdVerificationRequest(string IdPhotoKey);

    /// <param name="Phone">
    /// Blank or omitted routes the code to the account's own email instead — see
    /// <see cref="RequestPhoneOtpAsync"/>.
    /// </param>
    private sealed record RequestPhoneOtpRequest(string? Phone);

    private sealed record VerifyPhoneOtpRequest(string? Phone, string Code);

    private sealed record AddressResponse(
        string Id,
        string Label,
        string RecipientName,
        string Phone,
        string Line,
        string City,
        string? Province,
        string PostalCode,
        bool IsDefault);

    private sealed record BankAccountResponse(string BankName, string MaskedAccountNumber, string AccountHolderName);

    private sealed record ProfileResponse(
        string FullName,
        string? Phone,
        bool PhoneVerified,
        string? AvatarUrl,
        string IdVerificationStatus,
        BankAccountResponse? BankAccount,
        IReadOnlyList<AddressResponse> Addresses,
        StandingView? Standing = null);
}
