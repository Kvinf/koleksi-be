using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>
/// The rules for a seller's public handle: the name in <c>/toko/{handle}</c>, on listing cards
/// and in chat.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is not the sign-in name.</b> For a local or Cognito email account the sign-in name
/// <i>is</i> the email address, so showing it publicly leaks a private address and makes
/// <c>/toko/someone@mail.com</c> an unroutable URL. The public handle is a separate, URL-safe,
/// unique name that is never derived from the email, so it cannot be reversed to one.
/// </para>
/// <para>
/// Handles are stored lower-case and compared ordinally; <see cref="TryNormalize"/> is the single
/// place user input becomes one, so a lookup row keyed by a handle has exactly one spelling.
/// </para>
/// </remarks>
public static class PublicHandle
{
    public const int MinLength = 3;

    public const int MaxLength = 30;

    /// <summary>
    /// Characters a generated handle is drawn from. Lower-case letters and digits without the
    /// look-alikes (<c>0 o 1 l i</c>), so a handle read aloud or off a screenshot survives.
    /// </summary>
    private const string GeneratedAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    private const string GeneratedPrefix = "user-";

    private const int GeneratedSuffixLength = 8;

    /// <summary>
    /// Names that would impersonate the platform or collide with a route, a reserved word, or a
    /// system actor. Matched exactly, after normalisation.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "anonim", "anonymous", "api", "app", "assets", "auth", "bantuan",
        "beranda", "billing", "cart", "checkout", "contact", "dashboard", "help", "home", "info",
        "keluar", "keranjang", "login", "logout", "masuk", "media", "mod", "moderator", "null",
        "official", "order", "pesan", "pesanan", "profil", "profile", "root", "saya", "search",
        "sell", "settings", "shop", "signup", "staff", "support", "system", "toko", "undefined",
        "ulasan", "www",
    };

    /// <summary>The prefix no user may claim, because it would read as the platform itself.</summary>
    private const string PlatformPrefix = "koleksi";

    /// <summary>
    /// Whether a free-text name (a display name) would pass for the platform or its staff: a reserved
    /// word on its own, anything starting with the platform's name, or any word that claims authority.
    /// Compared on letters and digits only, so <c>Koleksi.id</c>, <c>KOLEKSI official</c> and
    /// <c>a d m i n</c> are all caught however they are spaced or punctuated.
    /// </summary>
    internal static bool Impersonates(string name) =>
        ImpersonatesSpelling(name) || LeetReadings(name).Exists(ImpersonatesSpelling);

    private static bool ImpersonatesSpelling(string name)
    {
        var compact = new StringBuilder(name.Length);
        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var ch in name.ToLowerInvariant())
        {
            if (IsAlphanumeric(ch))
            {
                compact.Append(ch);
                word.Append(ch);
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        var joined = compact.ToString();
        return Reserved.Contains(joined)
            || joined.StartsWith(PlatformPrefix, StringComparison.Ordinal)
            || words.Exists(AuthorityWords.Contains);
    }

    /// <summary>
    /// The ways a name can be read once look-alike characters are given their letters: <c>0</c> as o, <c>3</c> as e,
    /// <c>4</c> and <c>@</c> as a, <c>5</c> and <c>$</c> as s, <c>7</c> as t, and <c>1</c>, <c>!</c> and <c>|</c> as either i
    /// or l (hence two readings). <c>k0leksi</c> and <c>4dm1n</c> are then the words they are made to look like (QA P-05).
    /// </summary>
    private static List<string> LeetReadings(string name)
    {
        var asI = new StringBuilder(name.Length);
        var asL = new StringBuilder(name.Length);

        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is '1' or '!' or '|')
            {
                asI.Append('i');
                asL.Append('l');
                continue;
            }

            var folded = ch switch
            {
                '0' => 'o',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' => 't',
                _ => ch,
            };
            asI.Append(folded);
            asL.Append(folded);
        }

        return [asI.ToString(), asL.ToString()];
    }

    /// <summary>
    /// Whether a handle, read through look-alike characters and with separators dropped, is a reserved word or starts with
    /// the platform's name. <c>k0leksi</c>, <c>ko-leksi</c> and <c>4dm1n</c> are all caught.
    /// </summary>
    private static bool HandleLooksReserved(string candidate)
    {
        foreach (var reading in LeetReadings(candidate))
        {
            var compact = new string(reading.Where(IsAlphanumeric).ToArray());
            if (Reserved.Contains(compact) || compact.StartsWith(PlatformPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Words that, anywhere in a display name, claim to speak for the platform.</summary>
    private static readonly HashSet<string> AuthorityWords = new(StringComparer.Ordinal)
    {
        "admin", "administrator", "moderator", "staff", "support", "official", "system",
    };

    /// <summary>
    /// Trims and lower-cases <paramref name="raw"/> and checks it against every handle rule.
    /// </summary>
    /// <param name="raw">What the user typed, or what arrived in a URL.</param>
    /// <param name="handle">The canonical spelling when the result is <c>null</c>.</param>
    /// <returns>
    /// <c>null</c> when the handle is acceptable, otherwise a stable error code the frontend
    /// translates: <c>handle.invalid_format</c> or <c>handle.reserved</c>.
    /// </returns>
    public static string? TryNormalize(string? raw, out string handle)
    {
        handle = string.Empty;

        // ASCII-only lower-casing on purpose: the character set below is ASCII, so anything
        // that is not is rejected rather than folded into something that looks like it.
        var candidate = raw?.Trim().ToLowerInvariant() ?? string.Empty;

        if (candidate.Length is < MinLength or > MaxLength || !HasValidShape(candidate))
        {
            return "handle.invalid_format";
        }

        if (Reserved.Contains(candidate)
            || candidate.StartsWith(PlatformPrefix, StringComparison.Ordinal)
            || HandleLooksReserved(candidate))
        {
            return "handle.reserved";
        }

        handle = candidate;
        return null;
    }

    /// <summary>Whether <paramref name="value"/> is a handle exactly as stored (already canonical).</summary>
    public static bool IsCanonical(string? value) =>
        value is not null && TryNormalize(value, out var normalised) is null
            && string.Equals(normalised, value, StringComparison.Ordinal);

    /// <summary>
    /// Mints a random handle for an account that has none yet.
    /// </summary>
    /// <remarks>
    /// Random rather than derived from anything about the account, so it carries no information
    /// about the owner. Uniqueness is not assumed: the caller claims it with a conditional write
    /// and draws again on a collision.
    /// </remarks>
    public static string Generate()
    {
        Span<char> suffix = stackalloc char[GeneratedSuffixLength];
        for (var i = 0; i < suffix.Length; i++)
        {
            suffix[i] = GeneratedAlphabet[RandomNumberGenerator.GetInt32(GeneratedAlphabet.Length)];
        }

        return GeneratedPrefix + new string(suffix);
    }

    /// <summary>
    /// Letters and digits, with single hyphens or underscores allowed between them: it must start
    /// and end on a letter or digit, so a handle never looks like a flag or a path fragment.
    /// </summary>
    private static bool HasValidShape(string candidate)
    {
        if (!IsAlphanumeric(candidate[0]) || !IsAlphanumeric(candidate[^1]))
        {
            return false;
        }

        foreach (var ch in candidate)
        {
            if (!IsAlphanumeric(ch) && ch != '-' && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAlphanumeric(char ch) => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}

/// <summary>The optional name a seller shows in place of their handle.</summary>
public static class PublicDisplayName
{
    public const int MaxLength = 40;

    public const int MinLength = 2;

    /// <summary>
    /// Cleans <paramref name="raw"/> and checks it. A blank value is valid and means "show the
    /// handle".
    /// </summary>
    /// <returns><c>null</c> when acceptable, otherwise <c>profile.display_name_invalid</c>.</returns>
    public static string? TryNormalize(string? raw, out string? displayName)
    {
        displayName = null;

        // Collapse runs of whitespace and drop control and zero-width characters, so a pasted
        // name with a newline or an invisible character cannot break a card layout or a title.
        var builder = new StringBuilder(raw?.Length ?? 0);
        var pendingSpace = false;
        foreach (var ch in raw ?? string.Empty)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var cleaned = builder.ToString();

        if (cleaned.Length == 0)
        {
            return null;
        }

        // An address typed here would be published on every card; the sign-in name is never
        // meant to be public, so anything shaped like one is refused rather than trusted.
        if (cleaned.Length is < MinLength or > MaxLength || cleaned.Contains('@', StringComparison.Ordinal))
        {
            return "profile.display_name_invalid";
        }

        // The handle is held to the reserved-word list; the display name is what actually shows on
        // cards and in chat, so a name posing as the platform or its staff is refused the same way
        // (QA D-10). Same code: the screen already has copy for it.
        if (PublicHandle.Impersonates(cleaned))
        {
            return "profile.display_name_invalid";
        }

        displayName = cleaned;
        return null;
    }
}

/// <summary>
/// The public face of an account: the handle, the optional display name, and when it was
/// established. Everything on a seller's storefront that comes from the account itself.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="UserProfile"/>, which holds private details (address book, bank
/// account, phone). Nothing here is private by definition, and a separate record means a public
/// read can never be one added field away from serving something it should not.
/// </remarks>
public sealed class PublicProfile
{
    /// <summary>
    /// How long after a handle change the owner must wait to change it again.
    /// </summary>
    /// <remarks>
    /// A released handle can be claimed by anyone, so rapid renaming is a way to squat on names
    /// and to shed a reputation. The first change away from the auto-assigned handle is free.
    /// </remarks>
    public static readonly TimeSpan HandleChangeCooldown = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a handle the owner has left stays reserved for them before anyone else may take it.
    /// </summary>
    /// <remarks>
    /// Without it the old shop URL (<c>/toko/{handle}</c>) would be free the moment the owner
    /// renamed, and a stranger could claim it to inherit its links and reputation (QA D-10).
    /// </remarks>
    public static readonly TimeSpan HandleReservationPeriod = TimeSpan.FromDays(90);

    private PublicProfile(
        string userId,
        string handle,
        string? displayName,
        DateTimeOffset memberSince,
        DateTimeOffset? handleChangedAt,
        DateTimeOffset updatedAt,
        long version)
    {
        UserId = userId;
        Handle = handle;
        DisplayName = displayName;
        MemberSince = memberSince;
        HandleChangedAt = handleChangedAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    /// <summary>The Cognito subject. Server-side only; never in a response.</summary>
    public string UserId { get; }

    public string Handle { get; private set; }

    public string? DisplayName { get; private set; }

    /// <summary>When the account's public presence began (the profile's creation date when it has one).</summary>
    public DateTimeOffset MemberSince { get; }

    /// <summary>When the owner last changed the handle themselves; <c>null</c> while it is still the auto-assigned one.</summary>
    public DateTimeOffset? HandleChangedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>The name shown to people: the display name when the owner set one, otherwise the handle.</summary>
    public string ShownName => DisplayName ?? Handle;

    /// <summary>Creates the profile for an account's first handle.</summary>
    public static PublicProfile Create(string userId, string handle, DateTimeOffset memberSince, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(clock);

        if (!PublicHandle.IsCanonical(handle))
        {
            throw DomainException.For("handle.invalid_format", "The handle is not a valid public handle.");
        }

        return new PublicProfile(userId, handle, null, memberSince, null, clock.UtcNow, 0);
    }

    public static PublicProfile Rehydrate(
        string userId,
        string handle,
        string? displayName,
        DateTimeOffset memberSince,
        DateTimeOffset? handleChangedAt,
        DateTimeOffset updatedAt,
        long version) =>
        new(userId, handle, displayName, memberSince, handleChangedAt, updatedAt, version);

    /// <summary>
    /// Applies the owner's edit: a new handle, a new display name, or both.
    /// </summary>
    /// <param name="requestedHandle">The handle the owner typed. Unchanged is fine and is not a "change".</param>
    /// <param name="requestedDisplayName">The display name, or blank to clear it.</param>
    /// <exception cref="DomainException">
    /// <c>handle.invalid_format</c>, <c>handle.reserved</c>, <c>handle.change_too_soon</c> or
    /// <c>profile.display_name_invalid</c>.
    /// </exception>
    public void Edit(string? requestedHandle, string? requestedDisplayName, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var handleError = PublicHandle.TryNormalize(requestedHandle, out var newHandle);
        if (handleError is not null)
        {
            throw DomainException.For(handleError, "The handle is not acceptable.");
        }

        var nameError = PublicDisplayName.TryNormalize(requestedDisplayName, out var newDisplayName);
        if (nameError is not null)
        {
            throw DomainException.For(nameError, "The display name is not acceptable.");
        }

        var now = clock.UtcNow;

        if (!string.Equals(newHandle, Handle, StringComparison.Ordinal))
        {
            if (HandleChangedAt is { } changedAt && now - changedAt < HandleChangeCooldown)
            {
                throw DomainException.For(
                    "handle.change_too_soon", "The handle was changed recently. Try again later.");
            }

            Handle = newHandle;
            HandleChangedAt = now;
        }

        DisplayName = newDisplayName;
        UpdatedAt = now;
        Version++;
    }
}
