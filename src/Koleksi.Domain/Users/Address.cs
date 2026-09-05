using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>One shipping address on a profile.</summary>
/// <remarks>
/// A value object, not its own aggregate: an address has no lifecycle of its own outside
/// the profile that owns it, and nothing else in the domain ever references one by id
/// except the profile it lives on.
/// </remarks>
public readonly record struct Address(
    string Id,
    string Label,
    string RecipientName,
    string Phone,
    string Line,
    string City,
    string PostalCode,
    bool IsDefault)
{
    public const int MaxLabelLength = 40;
    public const int MaxLineLength = 240;

    /// <summary>
    /// Validates and normalises the fields a save-profile submission carries for one
    /// address row.
    /// </summary>
    /// <param name="id">
    /// The existing address's id when editing, or <c>null</c>/blank to mint a new one.
    /// </param>
    public static Address Create(
        string? id,
        string label,
        string recipientName,
        string phone,
        string line,
        string city,
        string postalCode,
        bool isDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(phone);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(city);
        ArgumentException.ThrowIfNullOrWhiteSpace(postalCode);

        if (label.Trim().Length > MaxLabelLength)
        {
            throw DomainException.For(
                "profile.address_label_too_long", $"Address label exceeds {MaxLabelLength} characters.");
        }

        if (line.Trim().Length > MaxLineLength)
        {
            throw DomainException.For(
                "profile.address_line_too_long", $"Street address exceeds {MaxLineLength} characters.");
        }

        var trimmedPostal = postalCode.Trim();
        if (trimmedPostal.Length != 5 || !trimmedPostal.All(char.IsAsciiDigit))
        {
            throw DomainException.For("profile.postal_code_invalid", "Postal code must be 5 digits.");
        }

        return new Address(
            string.IsNullOrWhiteSpace(id) ? EntityId.New() : id,
            label.Trim(),
            recipientName.Trim(),
            phone.Trim(),
            line.Trim(),
            city.Trim(),
            trimmedPostal,
            isDefault);
    }
}
