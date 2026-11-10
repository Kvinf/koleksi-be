using System.Text.RegularExpressions;

namespace Koleksi.Domain.Common;

/// <summary>
/// Spots phone numbers, email addresses and links in free text, so a buyer or seller cannot use a Dicari
/// title, description or proposal note to move the deal off the platform (and out of escrow).
/// </summary>
/// <remarks>
/// A heuristic, not a guarantee: someone determined can spell a number out in words. It catches the ordinary
/// cases cheaply, and the chat and reporting tools cover the rest. Prices written the Indonesian way
/// (<c>Rp 15.000.000</c>) are removed before the phone check, so a budget in a description does not trip it.
/// </remarks>
public static partial class ContactInfo
{
    /// <summary>Whether <paramref name="text"/> appears to contain a phone number, email address or link.</summary>
    public static bool Contains(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (EmailPattern().IsMatch(text) || LinkPattern().IsMatch(text))
        {
            return true;
        }

        // A dotted amount like 15.000.000 is a price, not a number to call.
        var withoutPrices = PricePattern().Replace(text, " ");
        return IndonesianMobilePattern().IsMatch(withoutPrices) || LongDigitRunPattern().IsMatch(withoutPrices);
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}._+\-]*\s?(?:@|\(at\)|\[at\])\s?[\p{L}\p{N}][\p{L}\p{N}\-]*\s?(?:\.|\(dot\)|\[dot\])\s?[\p{L}]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?:https?://|www\.)\S+|\b[a-z0-9\-]+\.(?:com|id|net|org|io|me|co|ly|link|app|shop|xyz|info|biz|site|store|online|to|gg|tv|cc)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{3})+\b", RegexOptions.CultureInvariant)]
    private static partial Regex PricePattern();

    [GeneratedRegex(@"(?<![\d])(?:\+?62|0)[\s\-.()]*8[\d\s\-.()]{7,}\d", RegexOptions.CultureInvariant)]
    private static partial Regex IndonesianMobilePattern();

    [GeneratedRegex(@"(?<![\d])\d(?:[\s\-.()]?\d){8,}(?![\d])", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitRunPattern();
}
