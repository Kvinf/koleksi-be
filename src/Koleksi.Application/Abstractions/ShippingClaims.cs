namespace Koleksi.Application.Abstractions;

/// <summary>
/// Remembers which order a shipment's proof photos and tracking number were used for, so none of them can
/// be used for a second order.
/// </summary>
/// <remarks>
/// <para>
/// The proof photos and the waybill are the evidence in a dispute. Checking only that a photo exists and
/// belongs to the seller lets a dishonest seller upload one set of photos once and ship every order with it.
/// A claim is one conditional write per value, all of them in one transaction, so two orders racing for the same
/// photo cannot both win.
/// </para>
/// <para>
/// A repeat for the <b>same</b> order succeeds: a retried request, or a shipment that failed after the claim,
/// must be able to run again. Claims are never released; they are small rows in the existing table and they are
/// the evidence that a photo belonged to one order.
/// </para>
/// </remarks>
public interface IShippingClaimStore
{
    /// <summary>
    /// Claims the waybill and every proof key for <paramref name="orderId"/>.
    /// </summary>
    /// <returns><c>null</c> when all were claimed (or were already this order's); otherwise what clashed.</returns>
    Task<ShippingClaimConflict?> TryClaimAsync(
        string orderId, string courierCode, string trackingNumber, IReadOnlyList<string> proofKeys, CancellationToken ct);
}

/// <summary>What a shipment tried to reuse.</summary>
public enum ShippingClaimConflict
{
    /// <summary>A proof photo already backs another order's shipment.</summary>
    ProofReused,

    /// <summary>The waybill is already on another order for the same courier.</summary>
    TrackingReused,
}
