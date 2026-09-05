using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Users;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="UserProfile"/> to and from its stored item.</summary>
/// <remarks>
/// Every attribute written here is read back in <see cref="ToDomain"/>; if you add one, add
/// it to both or the aggregate quietly loses the field on its next round trip — see the same
/// note on <c>ListingItem</c>.
/// </remarks>
internal static class ProfileItem
{
    public const string TypeName = "Profile";

    private const string IdAttr = "id";
    private const string FullNameAttr = "fullName";
    private const string PhoneAttr = "phone";
    private const string PhoneVerifiedAttr = "phoneVerified";
    private const string AvatarKeyAttr = "avatarKey";
    private const string IdVerificationStatusAttr = "idVerificationStatus";
    private const string BankNameAttr = "bankName";
    private const string BankAccountNumberAttr = "bankAccountNumber";
    private const string BankAccountHolderAttr = "bankAccountHolder";
    private const string AddressesAttr = "addresses";
    private const string CreatedAtAttr = "createdAt";
    private const string UpdatedAtAttr = "updatedAt";
    private const string VersionAttr = "version";

    private const string AddrIdAttr = "id";
    private const string AddrLabelAttr = "label";
    private const string AddrRecipientAttr = "recipient";
    private const string AddrPhoneAttr = "phone";
    private const string AddrLineAttr = "line";
    private const string AddrCityAttr = "city";
    private const string AddrPostalAttr = "postal";
    private const string AddrDefaultAttr = "isDefault";

    /// <summary>The concurrency token, named here so the conditional write can reference it.</summary>
    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> ToItem(UserProfile profile)
    {
        var addresses = new List<AttributeValue>(profile.Addresses.Count);
        foreach (var address in profile.Addresses)
        {
            addresses.Add(Attr.MapValue(new Dictionary<string, AttributeValue>(8, StringComparer.Ordinal)
            {
                [AddrIdAttr] = Attr.S(address.Id),
                [AddrLabelAttr] = Attr.S(address.Label),
                [AddrRecipientAttr] = Attr.S(address.RecipientName),
                [AddrPhoneAttr] = Attr.S(address.Phone),
                [AddrLineAttr] = Attr.S(address.Line),
                [AddrCityAttr] = Attr.S(address.City),
                [AddrPostalAttr] = Attr.S(address.PostalCode),
                [AddrDefaultAttr] = Attr.Bool(address.IsDefault),
            }));
        }

        var bank = profile.BankAccount;

        return new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.UserPk(profile.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.ProfileSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(profile.Id),
            [FullNameAttr] = Attr.S(profile.FullName),
            [PhoneAttr] = Attr.SOrNull(profile.Phone),
            [PhoneVerifiedAttr] = Attr.Bool(profile.PhoneVerified),
            [AvatarKeyAttr] = Attr.SOrNull(profile.AvatarKey),
            [IdVerificationStatusAttr] = Attr.EnumValue(profile.IdVerificationStatus),
            [BankNameAttr] = Attr.SOrNull(bank?.BankName),
            [BankAccountNumberAttr] = Attr.SOrNull(bank?.AccountNumber),
            [BankAccountHolderAttr] = Attr.SOrNull(bank?.AccountHolderName),
            [AddressesAttr] = Attr.ListValue(addresses),
            [CreatedAtAttr] = Attr.Instant(profile.CreatedAt),
            [UpdatedAtAttr] = Attr.Instant(profile.UpdatedAt),
            [VersionAttr] = Attr.N(profile.Version),
        };
    }

    public static UserProfile ToDomain(Dictionary<string, AttributeValue> item)
    {
        var addresses = new List<Address>();
        foreach (var entry in item.GetList(AddressesAttr))
        {
            if (entry.M is not { } map)
            {
                continue;
            }

            addresses.Add(new Address(
                map.GetString(AddrIdAttr),
                map.GetString(AddrLabelAttr),
                map.GetString(AddrRecipientAttr),
                map.GetString(AddrPhoneAttr),
                map.GetString(AddrLineAttr),
                map.GetString(AddrCityAttr),
                map.GetString(AddrPostalAttr),
                map.GetBool(AddrDefaultAttr)));
        }

        var bankName = item.GetStringOrNull(BankNameAttr);
        var bankAccountNumber = item.GetStringOrNull(BankAccountNumberAttr);
        var bankAccountHolder = item.GetStringOrNull(BankAccountHolderAttr);

        BankAccount? bank = bankName is not null && bankAccountNumber is not null && bankAccountHolder is not null
            ? new BankAccount(bankName, bankAccountNumber, bankAccountHolder)
            : null;

        return UserProfile.Rehydrate(
            id: item.GetString(IdAttr),
            fullName: item.GetStringOrNull(FullNameAttr) ?? string.Empty,
            phone: item.GetStringOrNull(PhoneAttr),
            phoneVerified: item.GetBool(PhoneVerifiedAttr),
            avatarKey: item.GetStringOrNull(AvatarKeyAttr),
            idVerificationStatus: item.GetEnum<IdVerificationStatus>(IdVerificationStatusAttr),
            bankAccount: bank,
            addresses: addresses,
            createdAt: item.GetInstant(CreatedAtAttr),
            updatedAt: item.GetInstant(UpdatedAtAttr),
            version: item.GetLongOrDefault(VersionAttr));
    }
}
