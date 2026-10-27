using Koleksi.Domain.Common;

namespace Koleksi.Domain.Users;

/// <summary>One shipping address on a profile.</summary>
/// <remarks>
/// <para>
/// A value object, not its own aggregate: an address has no lifecycle of its own outside
/// the profile that owns it, and nothing else in the domain ever references one by id
/// except the profile it lives on.
/// </para>
/// <para>
/// <b>Province</b> was added after the first addresses were stored, so it is nullable on the
/// type: a row written before then loads with <c>null</c>. New addresses must carry one
/// (<see cref="Create"/>); a legacy row can be kept as it is by passing
/// <c>provinceOptional: true</c>, which is how re-saving a profile does not force the owner to
/// fix every old address at once.
/// </para>
/// </remarks>
public readonly record struct Address(
    string Id,
    string Label,
    string RecipientName,
    string Phone,
    string Line,
    string City,
    string? Province,
    string PostalCode,
    bool IsDefault)
{
    public const int MaxLabelLength = 40;
    public const int MaxRecipientNameLength = 100;
    public const int MaxPhoneLength = 20;
    public const int MaxLineLength = 240;
    public const int MaxCityLength = 80;
    public const int MinProvinceLength = 2;
    public const int MaxProvinceLength = 80;

    /// <summary>
    /// Validates and normalises the fields a save-profile submission carries for one
    /// address row.
    /// </summary>
    /// <param name="id">
    /// The existing address's id when editing, or <c>null</c>/blank to mint a new one.
    /// </param>
    /// <param name="provinceOptional">
    /// <c>true</c> only for a row that was stored before provinces existed and is being
    /// re-saved unchanged in that respect. A blank province is then kept as <c>null</c>; a
    /// province that is supplied is still validated.
    /// </param>
    public static Address Create(
        string? id,
        string label,
        string recipientName,
        string phone,
        string line,
        string city,
        string? province,
        string postalCode,
        bool isDefault,
        bool provinceOptional = false)
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

        if (recipientName.Trim().Length > MaxRecipientNameLength)
        {
            throw DomainException.For(
                "profile.address_name_too_long", $"Recipient name exceeds {MaxRecipientNameLength} characters.");
        }

        if (phone.Trim().Length > MaxPhoneLength)
        {
            throw DomainException.For(
                "profile.address_phone_too_long", $"Phone number exceeds {MaxPhoneLength} characters.");
        }

        if (line.Trim().Length > MaxLineLength)
        {
            throw DomainException.For(
                "profile.address_line_too_long", $"Street address exceeds {MaxLineLength} characters.");
        }

        if (city.Trim().Length > MaxCityLength)
        {
            throw DomainException.For(
                "profile.address_city_too_long", $"City exceeds {MaxCityLength} characters.");
        }

        var trimmedProvince = province?.Trim();
        if (string.IsNullOrEmpty(trimmedProvince))
        {
            if (!provinceOptional)
            {
                throw DomainException.For("profile.province_required", "Province is required.");
            }

            trimmedProvince = null;
        }
        else if (trimmedProvince.Length is < MinProvinceLength or > MaxProvinceLength)
        {
            throw DomainException.For(
                "profile.province_invalid",
                $"Province must be {MinProvinceLength} to {MaxProvinceLength} characters.");
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
            trimmedProvince,
            trimmedPostal,
            isDefault);
    }
}
