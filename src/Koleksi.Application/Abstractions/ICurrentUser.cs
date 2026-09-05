namespace Koleksi.Application.Abstractions;

/// <summary>
/// The authenticated caller, as established from the validated access token.
/// </summary>
/// <remarks>
/// <para>
/// Every value here comes from token claims the API verified against Cognito's JWKS.
/// Nothing on this interface is ever populated from a request body, a header, or a
/// query parameter — that is the whole point of it existing. Handlers that need to
/// know "who is asking" take this dependency instead of accepting a user id argument,
/// so there is no code path where a caller can name someone else.
/// </para>
/// </remarks>
public interface ICurrentUser
{
    /// <summary>The Cognito subject, or <c>null</c> for an anonymous visitor.</summary>
    string? UserId { get; }

    /// <summary>The user's handle, which doubles as their shop name.</summary>
    string? Username { get; }

    bool IsAuthenticated { get; }

    /// <summary>Whether the caller holds the given Cognito group, e.g. <c>staff</c>.</summary>
    bool IsInRole(string role);

    /// <summary>
    /// Returns the caller's id, or throws when the request is anonymous.
    /// </summary>
    /// <remarks>
    /// Used by handlers that are unreachable without authentication, so a missing
    /// identity is a wiring bug rather than a user error. Endpoints still carry
    /// <c>RequireAuthorization</c>; this is the second lock on the same door.
    /// </remarks>
    string RequireUserId();
}
