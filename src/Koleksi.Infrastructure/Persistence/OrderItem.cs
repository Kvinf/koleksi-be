using Amazon.DynamoDBv2.Model;
using Koleksi.Domain.Orders;

namespace Koleksi.Infrastructure.Persistence;

/// <summary>Maps an <see cref="Order"/> to and from its stored item.</summary>
internal static class OrderItem
{
    public const string TypeName = "Order";

    private const string IdAttr = "id";
    private const string OrderNumberAttr = "orderNumber";
    private const string BuyerIdAttr = "buyerId";
    private const string SellerIdAttr = "sellerId";
    private const string LinesAttr = "lines";
    private const string ShipToAttr = "shipTo";
    private const string CourierAttr = "courier";
    private const string EscrowStateAttr = "escrowState";
    private const string ShipmentAttr = "shipment";
    private const string DisputeAttr = "dispute";
    private const string CreatedAtAttr = "createdAt";
    private const string PaidAtAttr = "paidAt";
    private const string ShippedAtAttr = "shippedAt";
    private const string DeliveredAtAttr = "deliveredAt";
    private const string ReleasedAtAttr = "releasedAt";
    private const string VersionAttr = "version";

    // Line fields
    private const string LineListingIdAttr = "listingId";
    private const string LineTitleAttr = "title";
    private const string LinePhotoKeyAttr = "photoKey";
    private const string LineUnitPriceAttr = "unitPrice";
    private const string LineQuantityAttr = "quantity";

    // ShippingAddress fields
    private const string AddrRecipientNameAttr = "recipientName";
    private const string AddrPhoneAttr = "phone";
    private const string AddrStreetAttr = "street";
    private const string AddrCityAttr = "city";
    private const string AddrProvinceAttr = "province";
    private const string AddrPostalCodeAttr = "postalCode";

    // CourierSelection fields
    private const string CourierCodeAttr = "courierCode";
    private const string CourierServiceNameAttr = "serviceName";
    private const string CourierCostAttr = "cost";
    private const string CourierEstimatedDaysAttr = "estimatedDays";

    // Shipment fields
    private const string ShipmentTrackingNumberAttr = "trackingNumber";
    private const string ShipmentPackedPhotoAttr = "packedParcelPhotoKey";
    private const string ShipmentLabelPhotoAttr = "trackingLabelPhotoKey";
    private const string ShipmentShippedOnAttr = "shippedOn";

    // Dispute fields
    private const string DisputeReasonAttr = "reason";
    private const string DisputeOpenedAtAttr = "openedAt";
    private const string DisputeResolutionAttr = "resolution";
    private const string DisputeResolvedAtAttr = "resolvedAt";

    public static string VersionAttribute => VersionAttr;

    public static Dictionary<string, AttributeValue> ToItem(Order order)
    {
        var createdAt = DynamoTable.FormatInstant(order.CreatedAt);

        var lines = new List<AttributeValue>(order.Lines.Count);
        foreach (var line in order.Lines)
        {
            lines.Add(Attr.MapValue(new Dictionary<string, AttributeValue>(5, StringComparer.Ordinal)
            {
                [LineListingIdAttr] = Attr.S(line.ListingId),
                [LineTitleAttr] = Attr.S(line.Title),
                [LinePhotoKeyAttr] = Attr.SOrNull(line.PhotoKey),
                [LineUnitPriceAttr] = Attr.MoneyValue(line.UnitPrice),
                [LineQuantityAttr] = Attr.N(line.Quantity),
            }));
        }

        var item = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DynamoTable.Pk] = Attr.S(DynamoTable.OrderPk(order.Id)),
            [DynamoTable.Sk] = Attr.S(DynamoTable.OrderMetaSk()),
            [DynamoTable.EntityType] = Attr.S(TypeName),

            [IdAttr] = Attr.S(order.Id),
            [OrderNumberAttr] = Attr.S(order.OrderNumber),
            [BuyerIdAttr] = Attr.S(order.BuyerId),
            [SellerIdAttr] = Attr.S(order.SellerId),
            [LinesAttr] = Attr.ListValue(lines),
            [ShipToAttr] = Attr.MapValue(ShipToToMap(order.ShipTo)),
            [CourierAttr] = Attr.MapValue(CourierToMap(order.Courier)),
            [EscrowStateAttr] = Attr.EnumValue(order.EscrowState),
            [ShipmentAttr] = order.Shipment is { } shipment
                ? Attr.MapValue(ShipmentToMap(shipment))
                : new AttributeValue { NULL = true },
            [DisputeAttr] = order.Dispute is { } dispute
                ? Attr.MapValue(DisputeToMap(dispute))
                : new AttributeValue { NULL = true },
            [CreatedAtAttr] = Attr.S(createdAt),
            [PaidAtAttr] = Attr.InstantOrNull(order.PaidAt),
            [ShippedAtAttr] = Attr.InstantOrNull(order.ShippedAt),
            [DeliveredAtAttr] = Attr.InstantOrNull(order.DeliveredAt),
            [ReleasedAtAttr] = Attr.InstantOrNull(order.ReleasedAt),
            [VersionAttr] = Attr.N(order.Version),

            [DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(order.SellerId)),
            [DynamoTable.SellerSk] = Attr.S(
                DynamoTable.SellerSkValue(DynamoTable.OrderIndexPrefix(), createdAt, order.Id)),
            [DynamoTable.BuyerPk] = Attr.S(DynamoTable.BuyerPkValue(order.BuyerId)),
            [DynamoTable.BuyerSk] = Attr.S(
                DynamoTable.BuyerSkValue(DynamoTable.OrderIndexPrefix(), createdAt, order.Id)),
        };

        return item;
    }

    public static Order ToDomain(Dictionary<string, AttributeValue> item)
    {
        var lines = new List<OrderLine>();
        foreach (var entry in item.GetList(LinesAttr))
        {
            if (entry.M is not { } map)
            {
                continue;
            }

            lines.Add(new OrderLine(
                map.GetString(LineListingIdAttr),
                map.GetString(LineTitleAttr),
                map.GetStringOrNull(LinePhotoKeyAttr),
                map.GetMoney(LineUnitPriceAttr),
                map.GetInt(LineQuantityAttr)));
        }

        var shipmentMap = item.TryGetValue(ShipmentAttr, out var shipmentValue) && shipmentValue.NULL != true
            ? shipmentValue.M
            : null;
        var disputeMap = item.TryGetValue(DisputeAttr, out var disputeValue) && disputeValue.NULL != true
            ? disputeValue.M
            : null;

        return Order.Rehydrate(
            id: item.GetString(IdAttr),
            orderNumber: item.GetString(OrderNumberAttr),
            buyerId: item.GetString(BuyerIdAttr),
            sellerId: item.GetString(SellerIdAttr),
            lines: lines,
            shipTo: ShipToFromMap(item.GetMap(ShipToAttr)),
            courier: CourierFromMap(item.GetMap(CourierAttr)),
            escrowState: item.GetEnum<EscrowState>(EscrowStateAttr),
            shipment: shipmentMap is null ? null : ShipmentFromMap(shipmentMap),
            dispute: disputeMap is null ? null : DisputeFromMap(disputeMap),
            createdAt: item.GetInstant(CreatedAtAttr),
            paidAt: item.GetInstantOrNull(PaidAtAttr),
            shippedAt: item.GetInstantOrNull(ShippedAtAttr),
            deliveredAt: item.GetInstantOrNull(DeliveredAtAttr),
            releasedAt: item.GetInstantOrNull(ReleasedAtAttr),
            version: item.GetLongOrDefault(VersionAttr));
    }

    private static Dictionary<string, AttributeValue> ShipToToMap(ShippingAddress address) =>
        new(StringComparer.Ordinal)
        {
            [AddrRecipientNameAttr] = Attr.S(address.RecipientName),
            [AddrPhoneAttr] = Attr.S(address.Phone),
            [AddrStreetAttr] = Attr.S(address.Street),
            [AddrCityAttr] = Attr.S(address.City),
            [AddrProvinceAttr] = Attr.S(address.Province),
            [AddrPostalCodeAttr] = Attr.S(address.PostalCode),
        };

    private static ShippingAddress ShipToFromMap(Dictionary<string, AttributeValue> map) => new(
        map.GetString(AddrRecipientNameAttr),
        map.GetString(AddrPhoneAttr),
        map.GetString(AddrStreetAttr),
        map.GetString(AddrCityAttr),
        map.GetString(AddrProvinceAttr),
        map.GetString(AddrPostalCodeAttr));

    private static Dictionary<string, AttributeValue> CourierToMap(CourierSelection courier) =>
        new(StringComparer.Ordinal)
        {
            [CourierCodeAttr] = Attr.S(courier.CourierCode),
            [CourierServiceNameAttr] = Attr.S(courier.ServiceName),
            [CourierCostAttr] = Attr.MoneyValue(courier.Cost),
            [CourierEstimatedDaysAttr] = Attr.N(courier.EstimatedDays),
        };

    private static CourierSelection CourierFromMap(Dictionary<string, AttributeValue> map) => new(
        map.GetString(CourierCodeAttr),
        map.GetString(CourierServiceNameAttr),
        map.GetMoney(CourierCostAttr),
        map.GetInt(CourierEstimatedDaysAttr));

    private static Dictionary<string, AttributeValue> ShipmentToMap(Shipment shipment) =>
        new(StringComparer.Ordinal)
        {
            [CourierCodeAttr] = Attr.S(shipment.CourierCode),
            [ShipmentTrackingNumberAttr] = Attr.S(shipment.TrackingNumber),
            [ShipmentPackedPhotoAttr] = Attr.S(shipment.PackedParcelPhotoKey),
            [ShipmentLabelPhotoAttr] = Attr.S(shipment.TrackingLabelPhotoKey),
            [ShipmentShippedOnAttr] = Attr.S(shipment.ShippedOn.ToString("yyyy-MM-dd")),
        };

    private static Shipment ShipmentFromMap(Dictionary<string, AttributeValue> map) => new(
        map.GetString(CourierCodeAttr),
        map.GetString(ShipmentTrackingNumberAttr),
        map.GetString(ShipmentPackedPhotoAttr),
        map.GetString(ShipmentLabelPhotoAttr),
        DateOnly.ParseExact(map.GetString(ShipmentShippedOnAttr), "yyyy-MM-dd"));

    private static Dictionary<string, AttributeValue> DisputeToMap(Dispute dispute)
    {
        var map = new Dictionary<string, AttributeValue>(StringComparer.Ordinal)
        {
            [DisputeReasonAttr] = Attr.S(dispute.Reason),
            [DisputeOpenedAtAttr] = Attr.Instant(dispute.OpenedAt),
            [DisputeResolutionAttr] = dispute.Resolution is { } resolution
                ? Attr.EnumValue(resolution)
                : new AttributeValue { NULL = true },
            [DisputeResolvedAtAttr] = Attr.InstantOrNull(dispute.ResolvedAt),
        };

        return map;
    }

    private static Dispute DisputeFromMap(Dictionary<string, AttributeValue> map) => new(
        map.GetString(DisputeReasonAttr),
        map.GetInstant(DisputeOpenedAtAttr),
        map.TryGetValue(DisputeResolutionAttr, out var resolution) && resolution.NULL != true
            ? Enum.Parse<DisputeResolution>(resolution.S)
            : null,
        map.GetInstantOrNull(DisputeResolvedAtAttr));
}
