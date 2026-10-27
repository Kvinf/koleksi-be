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

    /// <summary>The bucket applied to sending a chat message.</summary>
    /// <remarks>
    /// Per user, and well under the general budget: a message is free text delivered to another
    /// person, so the cost of abuse falls on someone who did not ask for it. Reading a thread
    /// is not in this bucket — a poll every few seconds must not spend a person's sending allowance.
    /// </remarks>
    public const string Messaging = "messaging";

    /// <summary>The bucket applied to saving and unsaving a listing (the heart).</summary>
    /// <remarks>
    /// Per user. Each call is a two-item transaction on the listing's row, so a script
    /// toggling hearts in a loop would also be contending with that listing's real traffic.
    /// The allowance is generous for a person ticking through a results page.
    /// </remarks>
    public const string Watchlist = "watchlist";

    /// <summary>The bucket applied to writing a review.</summary>
    /// <remarks>
    /// Per user and small: a review is public text about another person's business, and a buyer
    /// has no honest reason to write more than a handful in a few minutes.
    /// </remarks>
    public const string Reviews = "reviews";

    /// <summary>The bucket applied to minting a live-updates connect ticket.</summary>
    /// <remarks>
    /// Per user. A ticket is only useful for opening a connection, so the allowance covers a
    /// handful of tabs reconnecting together after a network drop, and no more.
    /// </remarks>
    public const string Realtime = "realtime";

    /// <summary>The bucket applied to search suggestions, which anyone can call without signing in.</summary>
    /// <remarks>
    /// Per client address. A person typing sends a request every few hundred milliseconds at most
    /// (the box waits for a pause), and repeats are answered from a short shared cache, so sixty a
    /// minute is generous for a person and still useless for a script trying to enumerate the catalogue.
    /// </remarks>
    public const string Suggest = "suggest";

    /// <summary>
    /// The tightest bucket in the product, applied to sign-in and sign-up.
    /// </summary>
    /// <remarks>
    /// Anonymous by definition, so it can only be keyed on the caller's address — and it
    /// has to be tight, because this is the one endpoint where guessing repeatedly is the
    /// whole attack.
    /// </remarks>
    public const string Authentication = "authentication";

    /// <summary>The bucket applied to starting a checkout (single item or cart).</summary>
    /// <remarks>
    /// Per user. Each checkout reserves items for a payment window and writes orders, so a script
    /// looping on it could hold the catalogue hostage; a real buyer makes a handful an hour.
    /// </remarks>
    public const string Checkout = "checkout";

    /// <summary>The bucket applied to asking for a phone or email verification code.</summary>
    /// <remarks>
    /// Per user and tight: every request sends a real SMS or email that costs money and lands on
    /// someone else's phone. The per-number cooldown and hourly cap in the verification throttle
    /// are the precise limits; this is the coarse bound in front of them.
    /// </remarks>
    public const string VerificationCode = "verification-code";

    /// <summary>The bucket applied to a signed-in user changing their own password.</summary>
    /// <remarks>
    /// Per user, not per address: the caller is authenticated, so the thing worth bounding is
    /// guesses at the current password by whoever holds a stolen access token, and that budget
    /// must follow the account across addresses.
    /// </remarks>
    public const string PasswordChange = "password-change";
}
