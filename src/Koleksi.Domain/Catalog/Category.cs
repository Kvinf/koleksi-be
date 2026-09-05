using Koleksi.Domain.Common;

namespace Koleksi.Domain.Catalog;

/// <summary>
/// A top-level collecting category, together with the filter schema that browse
/// renders for it.
/// </summary>
/// <remarks>
/// <para>
/// The handoff calls per-category filters "essential": cards filter by grade, set and
/// card language; sneakers by EU size, condition and brand; coins by mint state, year
/// and type. Modelling the schema as data rather than as code means adding a category
/// is a data change, not a deployment — and the frontend renders whatever facets it is
/// handed without knowing what a "mint state" is.
/// </para>
/// <para>
/// Categories are read on nearly every request and change perhaps monthly, so they are
/// cached in memory by the API for the lifetime of a Lambda execution environment.
/// </para>
/// </remarks>
public sealed class Category
{
    public Category(
        string id,
        string slug,
        LocalisedText name,
        IReadOnlyList<CategoryFacet> facets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentNullException.ThrowIfNull(facets);

        Id = id;
        Slug = slug;
        Name = name;
        Facets = facets;
    }

    /// <summary>Stable identifier, e.g. <c>cards</c>. Used as the DynamoDB partition prefix for browse.</summary>
    public string Id { get; }

    /// <summary>The URL segment, e.g. <c>kartu</c> in <c>/kartu/{slug}-{id}</c>.</summary>
    public string Slug { get; }

    public LocalisedText Name { get; }

    /// <summary>The filter groups browse renders for this category, in display order.</summary>
    public IReadOnlyList<CategoryFacet> Facets { get; }

    /// <summary>
    /// Rejects facet values a caller invented, so only vocabulary defined by the
    /// category reaches storage.
    /// </summary>
    /// <remarks>
    /// Facet values become part of DynamoDB index keys and are echoed into browse
    /// chips. Accepting arbitrary strings would let a seller pollute the facet
    /// namespace — inventing a <c>PSA 11</c> grade to sort above every honest listing —
    /// and would push unescaped user input into an indexed key. An allowlist drawn from
    /// the schema removes both problems.
    /// </remarks>
    public void EnsureAttributesAreValid(IReadOnlyDictionary<string, string> attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        foreach (var (key, value) in attributes)
        {
            var facet = Facets.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))
                ?? throw DomainException.For(
                    "category.unknown_facet",
                    $"'{key}' is not a filter on the {Id} category.");

            if (!facet.Options.Any(o => string.Equals(o.Value, value, StringComparison.Ordinal)))
            {
                throw DomainException.For(
                    "category.unknown_facet_value",
                    $"'{value}' is not a permitted value for the '{key}' filter.");
            }
        }
    }
}

/// <summary>One filter group in the browse sidebar, e.g. Grade or Size (EU).</summary>
/// <param name="Key">Stable facet id stored on the listing and used in the query string.</param>
/// <param name="Label">The group heading, translated.</param>
/// <param name="Options">The permitted values, in display order.</param>
public sealed record CategoryFacet(string Key, LocalisedText Label, IReadOnlyList<CategoryFacetOption> Options);

/// <summary>One selectable value within a facet.</summary>
/// <param name="Value">Stable value stored on the listing, e.g. <c>PSA 10</c>.</param>
/// <param name="Label">Display text, translated where the value is a word rather than a code.</param>
public sealed record CategoryFacetOption(string Value, LocalisedText Label);

/// <summary>
/// A string in both languages the product ships in.
/// </summary>
/// <param name="Id">Bahasa Indonesia — the primary language, per the handoff.</param>
/// <param name="En">English.</param>
/// <remarks>
/// Carrying both variants to the client lets the header's language toggle switch
/// instantly with no refetch, which is what the prototype demonstrates. Facet labels
/// are small and bounded, so sending both costs almost nothing.
/// </remarks>
public readonly record struct LocalisedText(string Id, string En)
{
    /// <summary>A term that is identical in both languages, such as a brand or grade code.</summary>
    public static LocalisedText Same(string value) => new(value, value);
}
