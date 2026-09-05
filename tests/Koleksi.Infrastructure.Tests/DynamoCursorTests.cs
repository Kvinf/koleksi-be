using System.Buffers.Text;
using System.Text;
using Amazon.DynamoDBv2.Model;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.Tests;

/// <summary>
/// The opaque paging cursor.
/// </summary>
/// <remarks>
/// <para>
/// A cursor is a client-supplied value that becomes a DynamoDB exclusive start key, so
/// two properties matter. It has to survive a round trip through a URL exactly — a key
/// that decodes to something slightly different silently skips or repeats a page — and
/// anything a caller sends that is not a cursor has to become <c>null</c>, not an
/// exception and not a partial key.
/// </para>
/// <para>
/// It is deliberately not signed. Every query it is used with takes its partition from
/// the caller's own identity or from a public category, so the worst a forged cursor can
/// do is start the caller's own result set in an odd place. That reasoning stops holding
/// the moment a query takes its partition from the cursor.
/// </para>
/// </remarks>
public sealed class DynamoCursorTests
{
    /// <summary>
    /// The separator the cursor puts between a field's type, name and value.
    /// </summary>
    /// <remarks>
    /// Written numerically, exactly as the implementation does. As a literal control
    /// character it would be invisible here and in every diff of this file.
    /// </remarks>
    private const char FieldSeparator = (char)0x1E;

    [Fact]
    public void A_key_survives_the_round_trip_unchanged()
    {
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["pk"] = new AttributeValue { S = "LISTING#01JQ8ZC4K7N2M5V9XR3P6TW0YB" },
            ["sk"] = new AttributeValue { S = "#META" },
        };

        var decoded = DynamoCursor.Decode(DynamoCursor.Encode(key)).ShouldNotBeNull();

        decoded.Count.ShouldBe(2);
        decoded["pk"].S.ShouldBe("LISTING#01JQ8ZC4K7N2M5V9XR3P6TW0YB");
        decoded["sk"].S.ShouldBe("#META");
    }

    [Fact]
    public void A_numeric_key_stays_numeric()
    {
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["expiresAt"] = new AttributeValue { N = "1800000000" },
        };

        var decoded = DynamoCursor.Decode(DynamoCursor.Encode(key)).ShouldNotBeNull();

        decoded["expiresAt"].N.ShouldBe("1800000000");
        decoded["expiresAt"].S.ShouldBeNull();
    }

    [Fact]
    public void A_key_value_containing_the_separators_used_elsewhere_still_round_trips()
    {
        // Keys are full of "#" and "-". The cursor separates its fields with ASCII unit
        // and record separators precisely because those cannot occur in a key attribute.
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["gsi_seller_sk"] = new AttributeValue { S = "Active#01JQ8ZC4K7N2M5V9XR3P6TW0YB" },
            ["gsi_browse_price_sk"] = new AttributeValue { S = "0000000001250000#01JQ8ZC4K7N2M5V9XR3P6TW0YB" },
        };

        var decoded = DynamoCursor.Decode(DynamoCursor.Encode(key)).ShouldNotBeNull();

        decoded["gsi_seller_sk"].S.ShouldBe("Active#01JQ8ZC4K7N2M5V9XR3P6TW0YB");
        decoded["gsi_browse_price_sk"].S.ShouldBe("0000000001250000#01JQ8ZC4K7N2M5V9XR3P6TW0YB");
    }

    [Fact]
    public void An_encoded_cursor_is_safe_in_a_url()
    {
        // Base64url, not base64: "+", "/" and "=" in a query string are re-encoded by some
        // clients and not by others, which turns a working link into an empty page.
        var key = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            ["pk"] = new AttributeValue { S = "LISTING#01JQ8ZC4K7N2M5V9XR3P6TW0YB" },
        };

        var cursor = DynamoCursor.Encode(key).ShouldNotBeNull();

        cursor.ShouldNotContain("+");
        cursor.ShouldNotContain("/");
        cursor.ShouldNotContain("=");
    }

    [Fact]
    public void The_first_page_has_no_cursor()
    {
        DynamoCursor.Encode(null).ShouldBeNull();
        DynamoCursor.Encode(new Dictionary<string, AttributeValue>(StringComparer.Ordinal)).ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-!!!")]
    [InlineData("////")]
    [InlineData("YWJjZGVm")]
    public void Anything_that_is_not_a_cursor_starts_from_the_beginning(string? cursor)
    {
        // A mangled cursor in a shared URL is a client error, not a server fault. Page one
        // is what a reader expects; a 500 is not.
        DynamoCursor.Decode(cursor).ShouldBeNull();
    }

    [Fact]
    public void A_cursor_that_decodes_but_is_the_wrong_shape_is_refused_whole()
    {
        // Refused whole rather than partially applied. Half a key is an exclusive start
        // key DynamoDB rejects at the far end of the call, with a message nobody can act
        // on — and a cursor is the one input a caller can put anything into.
        var truncated = $"S{FieldSeparator}pk";
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(truncated));

        DynamoCursor.Decode(cursor).ShouldBeNull();
    }
}
