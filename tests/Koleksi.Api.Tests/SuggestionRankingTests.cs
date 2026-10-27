using Koleksi.Application.Features.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>The pure matching and ordering behind search suggestions.</summary>
public sealed class SuggestionRankingTests
{
    private static SuggestionCandidate Candidate(string id, string title) => new(id, Slug.Words(title));

    [Fact]
    public void Every_typed_word_must_start_some_title_word()
    {
        SuggestionRanking.StartsAll(["psa", "1"], Slug.Words("PSA 10 Charizard")).ShouldBeTrue();
        SuggestionRanking.StartsAll(["psa", "9"], Slug.Words("PSA 10 Charizard")).ShouldBeFalse();
    }

    [Fact]
    public void Word_order_does_not_matter()
    {
        SuggestionRanking.StartsAll(["charizard", "psa"], Slug.Words("PSA 10 Charizard")).ShouldBeTrue();
    }

    [Fact]
    public void A_typed_word_matches_only_at_the_start_of_a_title_word_not_in_the_middle()
    {
        SuggestionRanking.StartsAll(["zard"], Slug.Words("Charizard")).ShouldBeFalse();
    }

    [Fact]
    public void Titles_that_begin_with_the_text_rank_first_then_newest_first()
    {
        var ranked = SuggestionRanking.Rank(
            ["char"],
            [
                Candidate("01A", "Base Set Charizard"),
                Candidate("01B", "Charizard Holo"),
                Candidate("01C", "Charmander Promo"),
                Candidate("01D", "Gundam"),
            ],
            take: 10);

        ranked.Select(candidate => candidate.Id).ShouldBe(["01C", "01B", "01A"]);
    }

    [Fact]
    public void The_same_listing_found_through_two_of_its_words_is_listed_once()
    {
        var ranked = SuggestionRanking.Rank(
            ["char"], [Candidate("01A", "Charizard Charmander"), Candidate("01A", "Charizard Charmander")], take: 10);

        ranked.Count.ShouldBe(1);
    }

    [Fact]
    public void The_list_is_cut_at_the_requested_size()
    {
        var many = Enumerable.Range(0, 20).Select(i => Candidate($"01{i:D2}", "Gundam " + i));

        SuggestionRanking.Rank(["gun"], many, take: 5).Count.ShouldBe(5);
    }

    [Fact]
    public void Terms_are_the_title_words_the_visitor_could_be_typing_most_common_first()
    {
        var terms = SuggestionRanking.Terms(
            ["cha"],
            [
                Candidate("01A", "Charizard Base"),
                Candidate("01B", "Charizard Holo"),
                Candidate("01C", "Charmander"),
            ],
            take: 3);

        terms.ShouldBe(["charizard", "charmander"]);
    }

    [Fact]
    public void Earlier_typed_words_stay_in_front_of_the_completed_word()
    {
        var terms = SuggestionRanking.Terms(["psa", "cha"], [Candidate("01A", "PSA 10 Charizard")], take: 3);

        terms.ShouldBe(["psa charizard"]);
    }
}
