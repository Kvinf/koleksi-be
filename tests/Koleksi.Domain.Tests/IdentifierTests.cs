using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class EntityIdTests
{
    [Fact]
    public void Generates_ids_of_the_documented_length_and_alphabet()
    {
        var id = EntityId.New();

        id.Length.ShouldBe(EntityId.Length);
        EntityId.IsValid(id).ShouldBeTrue();
        id.ShouldNotContain("-", Case.Sensitive);
        // Crockford excludes the glyphs most often misread.
        id.ShouldNotContain("I", Case.Sensitive);
        id.ShouldNotContain("L", Case.Sensitive);
        id.ShouldNotContain("O", Case.Sensitive);
        id.ShouldNotContain("U", Case.Sensitive);
    }

    [Fact]
    public void Ids_sort_chronologically_as_plain_strings()
    {
        // This is what lets DynamoDB range queries return "newest bids" with no index.
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var earlier = EntityId.New(start);
        var later = EntityId.New(start.AddSeconds(1));

        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
    }

    [Fact]
    public void Ids_generated_in_the_same_millisecond_are_distinct()
    {
        var at = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 500).Select(_ => EntityId.New(at)).ToHashSet(StringComparer.Ordinal);

        ids.Count.ShouldBe(500);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("01J8Z0000000000000000000000")]   // 27 characters
    [InlineData("01J8Z000000000000000000!0")]     // illegal character
    public void Rejects_anything_not_shaped_like_an_id(string? candidate) =>
        EntityId.IsValid(candidate).ShouldBeFalse();

    [Fact]
    public void Accepts_a_lowercase_id_and_normalises_it()
    {
        var id = EntityId.New();

        EntityId.IsValid(id.ToLowerInvariant()).ShouldBeTrue();
        EntityId.Normalise(id.ToLowerInvariant()).ShouldBe(id);
    }
}

public sealed class SlugTests
{
    [Theory]
    [InlineData("PSA 10 Charizard VMAX — Vivid Voltage", "psa-10-charizard-vmax-vivid-voltage")]
    [InlineData("One Piece OP-05 Monkey D. Luffy SEC — kondisi mint", "one-piece-op-05-monkey-d-luffy-sec-kondisi-mint")]
    [InlineData("Pokémon 151 booster bundle", "pokemon-151-booster-bundle")]
    [InlineData("   ", "item")]
    [InlineData("!!!", "item")]
    public void Builds_readable_url_fragments(string title, string expected) =>
        Slug.From(title).ShouldBe(expected);

    [Fact]
    public void Truncates_without_leaving_a_trailing_hyphen() =>
        Slug.From(new string('a', 40) + " " + new string('b', 60)).ShouldNotEndWith("-");

    [Fact]
    public void Extracts_the_id_from_a_slugged_path_segment()
    {
        var id = EntityId.New();

        Slug.ExtractId($"psa-10-charizard-vmax-{id}").ShouldBe(id);
    }

    [Fact]
    public void A_renamed_slug_still_resolves_the_same_listing()
    {
        // Titles change; inbound links must not break, and the slug must not be able to
        // point the URL at a different listing.
        var id = EntityId.New();

        Slug.ExtractId($"completely-different-title-{id}").ShouldBe(id);
    }

    [Theory]
    [InlineData("no-id-here")]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    public void Returns_null_for_segments_that_do_not_carry_an_id(string segment) =>
        Slug.ExtractId(segment).ShouldBeNull();
}
