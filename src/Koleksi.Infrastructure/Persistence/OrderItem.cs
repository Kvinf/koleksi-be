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
    private const string FeePercentAttr = "feePercent";
    private const string SourceAttr = "source";
    private const string SourceReferenceAttr = "sourceRef";
    private const string PaymentDueAtAttr = "paymentDueAt";
    private const string TrackingCheckedAtAttr = "trackingCheckedAt";

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

    /// <summary>The due-index state name for shipped parcels waiting to be asked about (one state for Shipped and InTransit).</summary>
    public const string TrackingState = "Tracking";

    public static string VersionAttribute => VersionAttr;

    public static string EscrowStateAttribute => EscrowStateAttr;

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

            // The commission rate this order was created under. Absent on rows written before it was
            // kept, which read as the current rate (Order.AppliedFeePercent).
            [FeePercentAttr] = order.FeePercentSnapshot is { } feePercent
                ? Attr.Dec(feePercent)
                : new AttributeValue { NULL = true },

            // Where the purchase came from and when it stops being payable. Both are absent on rows
            // written before they were kept, which read as a plain sale due 15 minutes after creation.
            [SourceAttr] = Attr.EnumValue(order.Source),
            [SourceReferenceAttr] = Attr.SOrNull(order.SourceReference),
            [PaymentDueAtAttr] = Attr.Instant(order.PaymentDueAt),
            [TrackingCheckedAtAttr] = Attr.InstantOrNull(order.TrackingCheckedAt),

            [DynamoTable.SellerPk] = Attr.S(DynamoTable.SellerPkValue(order.SellerId)),
            [DynamoTable.SellerSk] = Attr.S(
                DynamoTable.SellerSkValue(DynamoTable.OrderIndexPrefix(), createdAt, order.Id)),
            [DynamoTable.BuyerPk] = Attr.S(DynamoTable.BuyerPkValue(order.BuyerId)),
            [DynamoTable.BuyerSk] = Attr.S(
                DynamoTable.BuyerSkValue(DynamoTable.OrderIndexPrefix(), createdAt, order.Id)),
        };

        // The sweep's index entry. Sparse by state: an order carries these only while a clock is
        // running on it (the payment window while awaiting payment, the confirmation window while
        // delivered), and every other state writes the item without them. Orders are always written
        // as a whole item (AddManyAsync, UpdateAsync), so omitting the attributes is what removes the
        // entry; nothing patches an order in place.
        if (DueEntryFor(order) is { } due)
        {
            item[DynamoTable.AuctionEndPk] = Attr.S(DynamoTable.OrderDuePkValue(due.State, DynamoTable.ShardOf(order.Id)));
            item[DynamoTable.AuctionEndSk] = Attr.S(DynamoTable.OrderDueSkValue(due.DueAt, order.Id));
        }

        return item;
    }

    /// <summary>
    /// The sweep entry an order should hold right now, or <c>null</c> when no clock is running on it.
    /// </summary>
    /// <remarks>
    /// An awaiting-payment order is due once its payment deadline has passed. A delivered order is
    /// due at its auto-release time; a disputed one is not in <c>Delivered</c> and so is never swept,
    /// which is how an open dispute keeps funds frozen. A shipped parcel is due whenever the courier
    /// should next be asked about it (<see cref="Order.NextTrackingCheckAt"/>), and leaves the index the
    /// moment it is reported delivered.
    /// </remarks>
    internal static (string State, DateTimeOffset DueAt)? DueEntryFor(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return order.EscrowState switch
        {
            EscrowState.AwaitingPayment => (nameof(EscrowState.AwaitingPayment), order.PaymentDueAt),
            EscrowState.Delivered when order.AutoReleaseAt is { } releaseAt => (nameof(EscrowState.Delivered), releaseAt),
            EscrowState.Shipped or EscrowState.InTransit when order.NextTrackingCheckAt is { } checkAt => (TrackingState, checkAt),
            _ => null,
        };
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
            version: item.GetLongOrDefault(VersionAttr),
            platformFeePercent: item.GetDecimalOrNull(FeePercentAttr),
            source: item.TryGetValue(SourceAttr, out var source) && source.S is { Length: > 0 } sourceName
                && Enum.TryParse<OrderSource>(sourceName, out var parsedSource)
                    ? parsedSource
                    : OrderSource.FixedPrice,
            paymentDueAt: item.GetInstantOrNull(PaymentDueAtAttr),
            trackingCheckedAt: item.GetInstantOrNull(TrackingCheckedAtAttr),
            sourceReference: item.GetStringOrNull(SourceReferenceAttr));
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
