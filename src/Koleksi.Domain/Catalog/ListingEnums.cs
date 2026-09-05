namespace Koleksi.Domain.Catalog;

/// <summary>
/// How a listing is sold. The design surfaces these as the segmented control in
/// section 03 of the sell form: Fixed / Accept offers / Auction.
/// </summary>
public enum ListingFormat
{
    /// <summary>Buy now at the asking price. No negotiation.</summary>
    FixedPrice = 0,

    /// <summary>Buy now, or send an offer the seller can accept, counter or decline.</summary>
    AcceptOffers = 1,

    /// <summary>Timed auction (<c>lelang</c>). Price is the starting bid; there is no buy-now.</summary>
    Auction = 2,
}

/// <summary>Lifecycle of a listing.</summary>
public enum ListingStatus
{
    /// <summary>Saved but not visible to buyers. Never returned by public queries.</summary>
    Draft = 0,

    /// <summary>Live and purchasable.</summary>
    Active = 1,

    /// <summary>Bought or won. Stays reachable so inbound links and order history resolve.</summary>
    Sold = 2,

    /// <summary>Withdrawn by the seller. Hidden from browse, still reachable by direct link.</summary>
    Archived = 3,

    /// <summary>
    /// Removed by moderation — counterfeit, prohibited item, or a fraud signal.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Archived"/> on purpose: a seller may un-archive their
    /// own listing but may never lift a suspension. Suspended listings return 404 to
    /// everyone except the owner and staff.
    /// </remarks>
    Suspended = 4,
}
