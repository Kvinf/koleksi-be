using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Users;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps a <see cref="PublicProfile"/> and its handle claim to and from their stored items.</summary>
/// <remarks>
/// Every attribute written in <see cref="ToItem"/> is read back in <see cref="ToDomain"/>; add to
/// both or the aggregate loses the field on its next round trip.
/// </remarks>
internal static class PublicProfileItem
{
    public const string TypeName = "PublicProfile";

    public const string HandleTypeName = "Handle";

    public const string VersionAttribute = "version";

    public const string UserIdAttribute = "userId";

    /// <summary>Present on a handle row only while the handle is reserved after a rename: when the reservation ends.</summary>
    public const string ReservedUntilAttribute = "reservedUntil";

    private const string HandleAttr = "handle";
    private const string DisplayNameAttr = "displayName";
    private const string MemberSinceAttr = "memberSince";
    private const string HandleChangedAtAttr = "handleChangedAt";
    private const string UpdatedAtAttr = "updatedAt";

    public static Dictionary<string, AttributeValue> ToItem(PublicProfile profile) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.UserPk(profile.UserId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.PublicProfileSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [UserIdAttribute] = Attr.S(profile.UserId),
            [HandleAttr] = Attr.S(profile.Handle),
            [DisplayNameAttr] = Attr.SOrNull(profile.DisplayName),
            [MemberSinceAttr] = Attr.Instant(profile.MemberSince),
            [HandleChangedAtAttr] = Attr.InstantOrNull(profile.HandleChangedAt),
            [UpdatedAtAttr] = Attr.Instant(profile.UpdatedAt),
            [VersionAttribute] = Attr.N(profile.Version),
        };

    /// <summary>The lookup row that says who owns a handle.</summary>
    public static Dictionary<string, AttributeValue> ToHandleItem(string handle, string userId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.HandlePk(handle)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.HandleSk()),
            [DynamoTable.EntityType] = Attr.S(HandleTypeName),
            [UserIdAttribute] = Attr.S(userId),
        };

    public static PublicProfile ToDomain(Dictionary<string, AttributeValue> item) =>
        PublicProfile.Rehydrate(
            userId: item.GetString(UserIdAttribute),
            handle: item.GetString(HandleAttr),
            displayName: item.GetStringOrNull(DisplayNameAttr),
            memberSince: item.GetInstant(MemberSinceAttr),
            handleChangedAt: item.GetInstantOrNull(HandleChangedAtAttr),
            updatedAt: item.GetInstant(UpdatedAtAttr),
            version: item.GetLongOrDefault(VersionAttribute));

    public static Dictionary<string, AttributeValue> KeyOf(string userId) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.UserPk(userId)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.PublicProfileSk()),
        };

    public static Dictionary<string, AttributeValue> HandleKeyOf(string handle) =>
        new(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.HandlePk(handle)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.HandleSk()),
        };
}
