using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>
/// The six collecting categories and the filter schema each one renders.
/// </summary>
/// <remarks>
/// <para>
/// Taken verbatim from the handoff, which is explicit that per-category filters are
/// essential: cards filter by grade, set and card language; sneakers by EU size, condition
/// and brand; coins by mint state, year and type.
/// </para>
/// <para>
/// This is also a security boundary, not just a menu. <see cref="Category.EnsureAttributesAreValid"/>
/// checks every submitted facet against this allowlist, so a seller cannot invent a
/// <c>PSA 11</c> grade to sort above every honest listing, and cannot push arbitrary text
/// into an attribute name that becomes part of an indexed key.
/// </para>
/// </remarks>
internal static class CategorySeed
{
    public static IReadOnlyList<Category> All { get; } =
    [
        new Category("cards", "kartu", new LocalisedText("Kartu Koleksi", "Trading Cards"),
        [
            new CategoryFacet("grade", new LocalisedText("Grade", "Grade"),
            [
                // Grade codes are the same word in both languages — they are industry
                // notation, not translatable prose.
                new CategoryFacetOption("Grade 10", LocalisedText.Same("Grade 10")),
                new CategoryFacetOption("Grade 9", LocalisedText.Same("Grade 9")),
                new CategoryFacetOption("Grade 9.5", LocalisedText.Same("Grade 9.5")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("set", new LocalisedText("Set", "Set"),
            [
                new CategoryFacetOption("Base Set", LocalisedText.Same("Base Set")),
                new CategoryFacetOption("Neo Genesis", LocalisedText.Same("Neo Genesis")),
                new CategoryFacetOption("Evolving Skies", LocalisedText.Same("Evolving Skies")),
                new CategoryFacetOption("151", LocalisedText.Same("151")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("card_language", new LocalisedText("Bahasa Kartu", "Card Language"),
            [
                new CategoryFacetOption("Japanese", new LocalisedText("Jepang", "Japanese")),
                new CategoryFacetOption("English", new LocalisedText("Inggris", "English")),
                new CategoryFacetOption("Indonesian", new LocalisedText("Indonesia", "Indonesian")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),

        new Category("figures", "figur", new LocalisedText("Figur & Diecast", "Figures & Diecast"),
        [
            new CategoryFacet("scale", new LocalisedText("Skala", "Scale"),
            [
                new CategoryFacetOption("1/144", LocalisedText.Same("1/144")),
                new CategoryFacetOption("1/100", LocalisedText.Same("1/100")),
                new CategoryFacetOption("1/60", LocalisedText.Same("1/60")),
                new CategoryFacetOption("1/64", LocalisedText.Same("1/64")),
                new CategoryFacetOption("1/18", LocalisedText.Same("1/18")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("box_condition", new LocalisedText("Kondisi Box", "Box Condition"),
            [
                new CategoryFacetOption("Sealed", new LocalisedText("Segel", "Sealed")),
                new CategoryFacetOption("Opened", new LocalisedText("Terbuka", "Opened")),
                new CategoryFacetOption("No Box", new LocalisedText("Tanpa Box", "No Box")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("brand", new LocalisedText("Merek", "Brand"),
            [
                new CategoryFacetOption("Bandai", LocalisedText.Same("Bandai")),
                new CategoryFacetOption("Hot Wheels", LocalisedText.Same("Hot Wheels")),
                new CategoryFacetOption("Tomica", LocalisedText.Same("Tomica")),
                new CategoryFacetOption("Good Smile", LocalisedText.Same("Good Smile")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),

        new Category("sneakers", "sneakers", new LocalisedText("Sneakers", "Sneakers"),
        [
            new CategoryFacet("size_eu", new LocalisedText("Ukuran (EU)", "Size (EU)"),
            [
                new CategoryFacetOption("40", LocalisedText.Same("40")),
                new CategoryFacetOption("41", LocalisedText.Same("41")),
                new CategoryFacetOption("42", LocalisedText.Same("42")),
                new CategoryFacetOption("43", LocalisedText.Same("43")),
                new CategoryFacetOption("44", LocalisedText.Same("44")),
                new CategoryFacetOption("45", LocalisedText.Same("45")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("condition", new LocalisedText("Kondisi", "Condition"),
            [
                new CategoryFacetOption("BNIB", LocalisedText.Same("BNIB")),
                new CategoryFacetOption("VNDS", LocalisedText.Same("VNDS")),
                new CategoryFacetOption("Used", new LocalisedText("Bekas", "Used")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("brand", new LocalisedText("Merek", "Brand"),
            [
                new CategoryFacetOption("Nike", LocalisedText.Same("Nike")),
                new CategoryFacetOption("Adidas", LocalisedText.Same("Adidas")),
                new CategoryFacetOption("New Balance", LocalisedText.Same("New Balance")),
                new CategoryFacetOption("Asics", LocalisedText.Same("Asics")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),

        new Category("music", "musik", new LocalisedText("Musik & Game Retro", "Music & Retro Games"),
        [
            new CategoryFacet("media_grade", new LocalisedText("Grade Media", "Media Grade"),
            [
                new CategoryFacetOption("Mint", LocalisedText.Same("Mint")),
                new CategoryFacetOption("Near Mint", LocalisedText.Same("Near Mint")),
                new CategoryFacetOption("VG+", LocalisedText.Same("VG+")),
                new CategoryFacetOption("Good", new LocalisedText("Baik", "Good")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("format", new LocalisedText("Format", "Format"),
            [
                new CategoryFacetOption("Vinyl LP", LocalisedText.Same("Vinyl LP")),
                new CategoryFacetOption("Cassette", new LocalisedText("Kaset", "Cassette")),
                new CategoryFacetOption("CD", LocalisedText.Same("CD")),
                new CategoryFacetOption("Cartridge", new LocalisedText("Kartrid", "Cartridge")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("era", new LocalisedText("Era", "Era"),
            [
                new CategoryFacetOption("1970s", LocalisedText.Same("1970s")),
                new CategoryFacetOption("1980s", LocalisedText.Same("1980s")),
                new CategoryFacetOption("1990s", LocalisedText.Same("1990s")),
                new CategoryFacetOption("2000s", LocalisedText.Same("2000s")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),

        new Category("coins", "koin", new LocalisedText("Koin & Uang Kertas", "Coins & Banknotes"),
        [
            new CategoryFacet("mint_state", new LocalisedText("Kondisi Cetak", "Mint State"),
            [
                new CategoryFacetOption("UNC", LocalisedText.Same("UNC")),
                new CategoryFacetOption("AU", LocalisedText.Same("AU")),
                new CategoryFacetOption("XF", LocalisedText.Same("XF")),
                new CategoryFacetOption("VF", LocalisedText.Same("VF")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("year_issued", new LocalisedText("Tahun Terbit", "Year Issued"),
            [
                new CategoryFacetOption("Pre-1945", new LocalisedText("Sebelum 1945", "Pre-1945")),
                new CategoryFacetOption("1945-1965", LocalisedText.Same("1945-1965")),
                new CategoryFacetOption("1966-1998", LocalisedText.Same("1966-1998")),
                new CategoryFacetOption("Post-1998", new LocalisedText("Setelah 1998", "Post-1998")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("type", new LocalisedText("Jenis", "Type"),
            [
                new CategoryFacetOption("Coin", new LocalisedText("Koin", "Coin")),
                new CategoryFacetOption("Banknote", new LocalisedText("Uang Kertas", "Banknote")),
                new CategoryFacetOption("Commemorative", new LocalisedText("Peringatan", "Commemorative")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),

        new Category("watches", "jam", new LocalisedText("Jam & Kamera", "Watches & Cameras"),
        [
            new CategoryFacet("completeness", new LocalisedText("Kelengkapan", "Completeness"),
            [
                new CategoryFacetOption("Full Set", new LocalisedText("Full Set", "Full Set")),
                new CategoryFacetOption("Box Only", new LocalisedText("Box Saja", "Box Only")),
                new CategoryFacetOption("Papers Only", new LocalisedText("Surat Saja", "Papers Only")),
                new CategoryFacetOption("Watch Only", new LocalisedText("Unit Saja", "Watch Only")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("decade", new LocalisedText("Dekade", "Decade"),
            [
                new CategoryFacetOption("1960s", LocalisedText.Same("1960s")),
                new CategoryFacetOption("1970s", LocalisedText.Same("1970s")),
                new CategoryFacetOption("1980s", LocalisedText.Same("1980s")),
                new CategoryFacetOption("1990s", LocalisedText.Same("1990s")),
                new CategoryFacetOption("2000s", LocalisedText.Same("2000s")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
            new CategoryFacet("brand", new LocalisedText("Merek", "Brand"),
            [
                new CategoryFacetOption("Seiko", LocalisedText.Same("Seiko")),
                new CategoryFacetOption("Citizen", LocalisedText.Same("Citizen")),
                new CategoryFacetOption("Orient", LocalisedText.Same("Orient")),
                new CategoryFacetOption("Canon", LocalisedText.Same("Canon")),
                new CategoryFacetOption("Nikon", LocalisedText.Same("Nikon")),
                new CategoryFacetOption("Other", new LocalisedText("Lainnya", "Other")),
            ]),
        ]),
    ];
}

/// <summary>
/// Serves categories from <see cref="CategorySeed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Categories are read on nearly every request, change roughly monthly, and total a few
/// kilobytes. Serving them from memory removes a DynamoDB read from the hot path of every
/// browse, and removes an entire class of cold-start failure from it too.
/// </para>
/// <para>
/// The cost is that adding a category is a deployment. That is the right trade for now —
/// the facet schema is also the validation allowlist, so a change to it is a change to
/// what sellers may submit, and that deserves a review and a release rather than a console
/// edit. Moving to a table-backed implementation later means changing one DI registration;
/// nothing else refers to this type.
/// </para>
/// </remarks>
internal sealed class SeededCategoryRepository : ICategoryRepository
{
    private static readonly Dictionary<string, Category> ById =
        CategorySeed.All.ToDictionary(category => category.Id, StringComparer.Ordinal);

    private static readonly Dictionary<string, Category> BySlug =
        CategorySeed.All.ToDictionary(category => category.Slug, StringComparer.Ordinal);

    public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct) =>
        Task.FromResult(CategorySeed.All);

    public Task<Category?> FindByIdAsync(string categoryId, CancellationToken ct) =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(categoryId) ? null : ById.GetValueOrDefault(categoryId));

    public Task<Category?> FindBySlugAsync(string slug, CancellationToken ct) =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(slug) ? null : BySlug.GetValueOrDefault(slug));
}
