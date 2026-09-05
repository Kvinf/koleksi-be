namespace Koleksi.Api;

/// <summary>Named authorization policies.</summary>
/// <remarks>
/// Constants rather than string literals at each call site. A policy referenced by a
/// misspelled name is not a compile error and not a run-time error either — it simply
/// fails to match, and the endpoint it was meant to protect is protected by nothing but
/// the fallback policy.
/// </remarks>
public static class AuthorizationPolicies
{
    /// <summary>Platform staff: moderation, dispute resolution, listing suspension.</summary>
    public const string Staff = "staff";
}

/// <summary>Named CORS policies.</summary>
public static class CorsPolicies
{
    public const string Frontend = "frontend";
}

/// <summary>Named rate-limiting policies.</summary>
public static class RateLimitPolicies
{
    /// <summary>The tighter bucket applied to bidding.</summary>
    public const string Bidding = "bidding";

    /// <summary>
    /// The tightest bucket in the product, applied to sign-in and sign-up.
    /// </summary>
    /// <remarks>
    /// Anonymous by definition, so it can only be keyed on the caller's address — and it
    /// has to be tight, because this is the one endpoint where guessing repeatedly is the
    /// whole attack.
    /// </remarks>
    public const string Authentication = "authentication";
}
