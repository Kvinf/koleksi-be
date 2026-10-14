using System.Text;

namespace Koleksi.Domain.Common;

/// <summary>
/// A URL-safe fragment derived from a human title, used to build the crawlable
/// item URLs the design requires: <c>/{category}/{slug}-{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The slug is decorative — the trailing id is what actually resolves the listing.
/// That means a seller editing a title cannot break inbound links or be used to
/// hijack another listing's URL, and it removes any need for slug uniqueness checks.
/// </para>
/// <para>
/// <b>No dependency on ICU.</b> The obvious implementation decomposes accents with
/// <see cref="string.Normalize(NormalizationForm)"/> and drops the combining marks.
/// That silently does nothing under <c>InvariantGlobalization</c>, which the API
/// enables to keep Lambda cold starts down — "Pokémon" would slug to
/// <c>pok-mon</c> in production while passing every test on a developer machine.
/// The transliteration table below is explicit and behaves identically everywhere.
/// </para>
/// </remarks>
public static class Slug
{
    private const int MaxLength = 80;

    /// <summary>
    /// Converts a title into a lowercase, hyphen-separated slug.
    /// Returns <c>"item"</c> when nothing usable survives (a title that is entirely
    /// CJK or emoji, for instance).
    /// </summary>
    public static string From(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var builder = new StringBuilder(title.Length);
        var pendingSeparator = false;

        foreach (var raw in title)
        {
            var mapped = ToAsciiEquivalent(char.ToLowerInvariant(raw));

            if (mapped is null)
            {
                // Anything with no ASCII equivalent becomes a word break. Runs of them
                // collapse, and a trailing one is dropped, so we never emit "--" or "-$".
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                builder.Append('-');
                pendingSeparator = false;
            }

            builder.Append(mapped);

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxLength)
        {
            slug = slug[..MaxLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "item" : slug;
    }

    /// <summary>
    /// Splits text into lowercase ASCII words, folding accents with the same table as
    /// <see cref="From"/>. Punctuation, symbols and whitespace are word breaks, so
    /// <c>"Hot-Wheels #5 (Pokémon)"</c> gives <c>hot, wheels, 5, pokemon</c>.
    /// </summary>
    /// <remarks>
    /// This is what search suggestions match on, so a visitor typing <c>pokemon</c> finds
    /// "Pokémon". Unlike <see cref="From"/> there is no length cap and no placeholder: text with
    /// no usable character gives an empty list.
    /// </remarks>
    public static IReadOnlyList<string> Words(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var raw in text)
        {
            var mapped = ToAsciiEquivalent(char.ToLowerInvariant(raw));
            if (mapped is not null)
            {
                current.Append(mapped);
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>
    /// Pulls the id out of a <c>{slug}-{id}</c> path segment, ignoring the slug.
    /// </summary>
    /// <remarks>
    /// Ids never contain a hyphen (see <see cref="EntityId"/>), so the id is simply
    /// the final hyphen-separated segment. Returns <c>null</c> for anything that is
    /// not shaped like an id, which the endpoint turns into a 404 without ever
    /// reaching the data store.
    /// </remarks>
    public static string? ExtractId(string slugWithId)
    {
        if (string.IsNullOrWhiteSpace(slugWithId))
        {
            return null;
        }

        var lastHyphen = slugWithId.LastIndexOf('-');
        var candidate = lastHyphen < 0 ? slugWithId : slugWithId[(lastHyphen + 1)..];

        return EntityId.IsValid(candidate) ? EntityId.Normalise(candidate) : null;
    }

    /// <summary>
    /// Maps one lowercase character to its ASCII slug equivalent, or <c>null</c> when
    /// it should be treated as a word break.
    /// </summary>
    /// <remarks>
    /// Covers Latin-1 Supplement and the parts of Latin Extended-A that turn up in
    /// this marketplace's titles — brand names, Japanese romanisation, and European
    /// spellings such as "Pokémon", "Citroën" and "Nürburgring".
    /// </remarks>
    private static string? ToAsciiEquivalent(char ch) => ch switch
    {
        >= 'a' and <= 'z' => ch.ToString(),
        >= '0' and <= '9' => ch.ToString(),

        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' or 'ā' or 'ă' or 'ą' => "a",
        'æ' => "ae",
        'ç' or 'ć' or 'č' or 'ĉ' or 'ċ' => "c",
        'ď' or 'đ' => "d",
        'è' or 'é' or 'ê' or 'ë' or 'ē' or 'ĕ' or 'ė' or 'ę' or 'ě' => "e",
        'ĝ' or 'ğ' or 'ġ' or 'ģ' => "g",
        'ĥ' or 'ħ' => "h",
        'ì' or 'í' or 'î' or 'ï' or 'ĩ' or 'ī' or 'ĭ' or 'į' or 'ı' => "i",
        'ĵ' => "j",
        'ķ' => "k",
        'ĺ' or 'ļ' or 'ľ' or 'ŀ' or 'ł' => "l",
        'ñ' or 'ń' or 'ņ' or 'ň' => "n",
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' or 'ø' or 'ō' or 'ŏ' or 'ő' => "o",
        'œ' => "oe",
        'ŕ' or 'ŗ' or 'ř' => "r",
        'ś' or 'ŝ' or 'ş' or 'š' => "s",
        'ß' => "ss",
        'ţ' or 'ť' or 'ŧ' => "t",
        'ù' or 'ú' or 'û' or 'ü' or 'ũ' or 'ū' or 'ŭ' or 'ů' or 'ű' or 'ų' => "u",
        'ŵ' => "w",
        'ý' or 'ÿ' or 'ŷ' => "y",
        'ź' or 'ż' or 'ž' => "z",

        _ => null,
    };
}
