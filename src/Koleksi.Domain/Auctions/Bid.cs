using Koleksi.Domain.Common;

namespace Koleksi.Domain.Auctions;

/// <summary>One entry in an auction's bid history.</summary>
/// <param name="Id">Time-ordered id; also the DynamoDB sort key, so bids read back newest-first.</param>
/// <param name="AuctionId">The auction this bid belongs to.</param>
/// <param name="BidderId">Cognito subject of the bidder. Never leaves the server.</param>
/// <param name="BidderUsername">The bidder's handle, masked before display.</param>
/// <param name="Amount">The amount this bid moved the price to.</param>
/// <param name="PlacedAt">When the bid was accepted.</param>
public sealed record Bid(
    string Id,
    string AuctionId,
    string BidderId,
    string BidderUsername,
    Money Amount,
    DateTimeOffset PlacedAt);

/// <summary>
/// Masks bidder handles for the public bid history.
/// </summary>
/// <remarks>
/// <para>
/// The design shows bidders as <c>ran•••hw</c>. This is a privacy control, so it
/// belongs in the domain rather than in a view helper: bid history is public on every
/// auction page, and an unmasked history would publish who is spending what, let rivals
/// target a known collector, and make shill-bidding rings trivial to coordinate.
/// </para>
/// <para>
/// The mask keeps the first three and last two characters. That is enough for a bidder
/// to recognise themselves without letting an observer enumerate the user base.
/// </para>
/// </remarks>
public static class BidderAlias
{
    private const int LeadingVisible = 3;
    private const int TrailingVisible = 2;
    private const string Ellipsis = "•••";

    public static string Mask(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return Ellipsis;
        }

        var trimmed = username.Trim();

        // Too short to mask meaningfully: revealing three of four characters would
        // defeat the purpose, so hide everything but the first.
        if (trimmed.Length <= LeadingVisible + TrailingVisible)
        {
            return trimmed[..1] + Ellipsis;
        }

        return string.Concat(
            trimmed.AsSpan(0, LeadingVisible),
            Ellipsis,
            trimmed.AsSpan(trimmed.Length - TrailingVisible));
    }
}
