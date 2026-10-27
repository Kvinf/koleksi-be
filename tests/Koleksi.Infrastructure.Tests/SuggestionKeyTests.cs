using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>Key composition for the search-suggestion rows.</summary>
public sealed class SuggestionKeyTests
{
    private const string ListingId = "01JQ8ZC4K7N2M5V9XR3P6TW0YB";

    [Fact]
    public void Words_are_bucketed_by_their_first_two_letters()
    {
        DynamoTable.SuggestPkValue("charizard").ShouldBe("SUGGEST#ch");
        DynamoTable.SuggestPkValue("charmander").ShouldBe("SUGGEST#ch");
        DynamoTable.SuggestPkValue("pokemon").ShouldBe("SUGGEST#po");
    }

    [Fact]
    public void A_word_of_exactly_two_letters_uses_the_whole_word()
    {
        DynamoTable.SuggestPkValue("xl").ShouldBe("SUGGEST#xl");
    }

    [Fact]
    public void A_typed_prefix_lands_in_the_same_bucket_as_the_words_it_could_become()
    {
        // The lookup computes the bucket from what was typed so far; it must be the bucket the full
        // word was written to, for every prefix of two letters or more.
        foreach (var typed in new[] { "ch", "cha", "char", "chari", "charizard" })
        {
            DynamoTable.SuggestPkValue(typed).ShouldBe(DynamoTable.SuggestPkValue("charizard"));
        }
    }

    [Fact]
    public void The_sort_key_is_the_word_then_the_listing_so_one_word_sits_together()
    {
        var key = DynamoTable.SuggestSkValue("charizard", ListingId);

        key.ShouldBe("charizard#" + ListingId);
        key.ShouldStartWith("charizard");
    }

    [Fact]
    public void Suggestion_rows_never_share_a_partition_with_browse_or_listings()
    {
        DynamoTable.SuggestPkValue("gundam").ShouldNotStartWith("BROWSE#");
        DynamoTable.SuggestPkValue("gundam").ShouldNotStartWith("LISTING#");
    }

    [Fact]
    public void An_empty_word_is_refused_rather_than_written_to_the_bare_prefix()
    {
        Should.Throw<ArgumentException>(() => DynamoTable.SuggestPkValue(""));
    }
}
