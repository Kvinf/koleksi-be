using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

/// <summary>The word splitting that search suggestions are matched on.</summary>
public sealed class SlugWordsTests
{
    [Fact]
    public void Text_is_lowercased_and_split_on_anything_that_is_not_a_letter_or_digit()
    {
        Slug.Words("Hot-Wheels #5 (Limited)").ShouldBe(["hot", "wheels", "5", "limited"]);
    }

    [Fact]
    public void Accents_are_folded_without_ICU_so_pokemon_finds_Pokemon()
    {
        // string.Normalize does nothing under InvariantGlobalization, which is why the explicit table exists.
        Slug.Words("Pokémon Nürburgring Citroën").ShouldBe(["pokemon", "nurburgring", "citroen"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Text_with_nothing_usable_gives_no_words_rather_than_a_placeholder(string? text)
    {
        Slug.Words(text).ShouldBeEmpty();
    }

    [Fact]
    public void There_is_no_length_cap_so_a_long_title_keeps_its_last_words()
    {
        var words = Slug.Words(string.Join(' ', Enumerable.Repeat("gundam", 40)) + " zeta");

        words.Count.ShouldBe(41);
        words[^1].ShouldBe("zeta");
    }
}
