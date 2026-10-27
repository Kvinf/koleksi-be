using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Microsoft.Extensions.Primitives;

namespace Koleksi.AdminApi.Security;

/// <summary>
/// What a signed-in staff member may do, one named capability at a time.
/// </summary>
/// <remarks>
/// There is one role today: every staff member holds every capability. The policies are still one per
/// capability, and every endpoint names the capability it needs, so splitting support, moderation and finance
/// later means changing <see cref="StaffCapabilities.IsGranted"/> and nothing else.
/// </remarks>
public static class AdminCapabilities
{
    /// <summary>Start a session: the web tier calls this once after a successful sign-in.</summary>
    public const string SessionStart = "admin.session.start";

    /// <summary>Read the audit log.</summary>
    public const string AuditRead = "admin.audit.read";

    /// <summary>The overview page's queue counts.</summary>
    public const string OverviewRead = "admin.overview.read";

    /// <summary>List and open disputes (opening a case with chat is itself audited).</summary>
    public const string DisputesRead = "admin.disputes.read";

    /// <summary>Add a note, or ask a side for information.</summary>
    public const string DisputesNote = "admin.disputes.note";

    /// <summary>Rule on a dispute: release to the seller or refund the buyer.</summary>
    public const string DisputesResolve = "admin.disputes.resolve";

    public const string HolidaysRead = "admin.holidays.read";

    public const string HolidaysEdit = "admin.holidays.edit";

    public static IReadOnlyList<string> All { get; } =
    [
        SessionStart, AuditRead, OverviewRead, DisputesRead, DisputesNote, DisputesResolve, HolidaysRead, HolidaysEdit,
    ];
}

/// <summary>Maps a staff principal to the capabilities it holds.</summary>
public static class StaffCapabilities
{
    /// <summary>
    /// The single place that decides who may do what. A token that reached this point was already
    /// validated against the staff pool and its app client, so any authenticated principal is staff.
    /// </summary>
    public static bool IsGranted(ClaimsPrincipal principal, string capability)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);

        return principal.Identity?.IsAuthenticated == true && AdminCapabilities.All.Contains(capability);
    }
}

/// <summary>Named rate-limit policies of the admin API.</summary>
public static class AdminRateLimits
{
    /// <summary>Anything that writes. Per staff member, and small: a person clicking cannot reach it.</summary>
    public const string Write = "admin-write";
}

/// <summary>
/// Reads the staff member from the validated access token on the current request. Nothing here comes from a
/// header, query string or body.
/// </summary>
public sealed class CurrentStaff(IHttpContextAccessor accessor) : ICurrentStaff
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public string StaffId =>
        Principal.FindFirstValue("sub")
        ?? throw DomainException.For("auth.required", "This action requires a staff sign-in.");

    /// <summary>
    /// A Cognito access token carries the sign-in name as <c>username</c> (the id token's <c>cognito:username</c> is
    /// accepted too, for tokens from the local development identity).
    /// </summary>
    public string StaffName =>
        Principal.FindFirstValue("username")
        ?? Principal.FindFirstValue("cognito:username")
        ?? throw DomainException.For("auth.required", "This action requires a staff sign-in.");

    public string RequestId => accessor.HttpContext?.TraceIdentifier ?? "none";
}

/// <summary>
/// Answers 404 to any request that does not carry the web tier's shared secret, before authentication runs.
/// </summary>
/// <remarks>
/// <para>
/// The admin site's IP allowlist is enforced at its own CloudFront distribution, but this API has a public
/// API Gateway address of its own. Without this gate a staff token stolen from anywhere could be replayed
/// straight at that address. With it, the token is useless unless the request also comes from the web tier,
/// which the allowlist guards.
/// </para>
/// <para>
/// 404, not 401, so that a probe learns nothing about whether a route exists. The comparison is of SHA-256
/// hashes in fixed time, so neither the secret's length nor how much of it matched can be timed. With no
/// secret configured (a developer's machine only: a deployed stage refuses to start without one) the gate is off.
/// </para>
/// </remarks>
public sealed class AdminGateMiddleware(RequestDelegate next, string? secret)
{
    public const string HeaderName = "X-Koleksi-Admin-Internal";

    private readonly byte[]? secretHash = string.IsNullOrWhiteSpace(secret) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (secretHash is null || IsExempt(context.Request.Path) || Matches(context.Request.Headers[HeaderName]))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    /// <summary>The liveness check reveals nothing and is what the platform's own monitoring calls.</summary>
    private static bool IsExempt(PathString path) => path.Equals("/health", StringComparison.OrdinalIgnoreCase);

    private bool Matches(StringValues presented) =>
        secretHash is not null
        && presented.Count == 1
        && !string.IsNullOrEmpty(presented[0])
        && CryptographicOperations.FixedTimeEquals(secretHash, SHA256.HashData(Encoding.UTF8.GetBytes(presented[0]!)));
}

/// <summary>
/// Response headers for an API that only ever answers JSON to a server. The same set as the public API, plus the
/// two that matter more here: nothing may index it and nothing may cache it.
/// </summary>
public sealed class AdminSecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] =
                "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["X-Robots-Tag"] = "noindex, nofollow";
            headers["Cache-Control"] = "private, no-store";
            headers.Remove("Server");
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
