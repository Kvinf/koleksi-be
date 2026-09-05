using System.Security.Claims;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;

namespace Koleksi.Api.Security;

/// <summary>
/// Reads the caller's identity from the validated access token on the current request.
/// </summary>
/// <remarks>
/// <para>
/// Every value comes from <see cref="ClaimsPrincipal"/>, which the JWT bearer handler
/// populates only after verifying the token's RS256 signature against Cognito's JWKS,
/// its issuer, its audience, and its expiry. There is no path here that reads a header,
/// a query parameter, or a body field.
/// </para>
/// <para>
/// A missing <see cref="HttpContext"/> yields an anonymous user rather than throwing,
/// so background work sharing the same service graph is treated as unauthenticated
/// instead of accidentally inheriting someone's identity.
/// </para>
/// </remarks>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <summary>Cognito places the user pool's stable subject identifier in <c>sub</c>.</summary>
    private const string SubjectClaim = "sub";

    /// <summary>
    /// Cognito's preferred username claim. This is the handle that doubles as the shop
    /// name, per the sign-up form's "this is your shop name" hint.
    /// </summary>
    private const string UsernameClaim = "cognito:username";

    /// <summary>Cognito emits group membership as a repeated <c>cognito:groups</c> claim.</summary>
    private const string GroupsClaim = "cognito:groups";

    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public string? UserId => Principal?.FindFirstValue(SubjectClaim);

    public string? Username =>
        Principal?.FindFirstValue(UsernameClaim)
        ?? Principal?.FindFirstValue(ClaimTypes.Name);

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId is not null;

    public bool IsInRole(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        var principal = Principal;
        if (principal is null)
        {
            return false;
        }

        // Ordinal comparison: group names are opaque identifiers, not display text, and
        // a culture-aware comparison can equate strings that are not the same group.
        return principal.FindAll(GroupsClaim)
            .Any(claim => string.Equals(claim.Value, role, StringComparison.Ordinal));
    }

    public string RequireUserId() => UserId
        ?? throw DomainException.For("auth.required", "This action requires you to be signed in.");
}
