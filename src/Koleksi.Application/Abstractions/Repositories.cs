using System.Text.Json.Serialization;
using Koleksi.Application.Common;
using Koleksi.Domain.Auctions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.Application.Abstractions;

/// <summary>
/// Raised when an optimistic-concurrency write loses a race and the caller should
/// re-read and retry.
/// </summary>
/// <remarks>
/// Two people bidding on the same lot in the same second is normal traffic, not an
/// error. The API translates this into a retry, and surfaces a <c>409</c> only after
/// the retries are exhausted.
/// </remarks>
public sealed class ConcurrencyConflictException(string message) : Exception(message);

/// <summary>Reads and writes listings.</summary>
public interface IListingRepository
{
    Task<Listing?> FindAsync(string listingId, CancellationToken ct);

    /// <summary>Adds a listing, failing if the id already exists.</summary>
    Task AddAsync(Listing listing, CancellationToken ct);

    /// <summary>Saves a listing, failing if it changed since it was read.</summary>
    /// <param name="expectedVersion">
    /// The listing's <see cref="Listing.Version"/> as it was when the caller read it,
    /// captured before any mutation. Passed explicitly rather than inferred from the
    /// aggregate, because a caller that applied two changes has already incremented the
    /// version twice and the repository cannot tell how far to count back.
    /// </param>
    /// <exception cref="ConcurrencyConflictException">The listing changed concurrently.</exception>
    Task UpdateAsync(Listing listing, long expectedVersion, CancellationToken ct);

    /// <summary>A seller's own listings, including drafts. Used by the dashboard.</summary>
    Task<Page<Listing>> ListBySellerAsync(
        string sellerId, ListingStatus? status, string? cursor, int limit, CancellationToken ct);

    /// <summary>
    /// Bumps the view counter without reading the listing first.
    /// </summary>
    /// <remarks>
    /// An unconditional atomic add. Deliberately not part of the optimistic-concurrency
    /// path: a page view must never fail a seller's concurrent edit, and a lost view is
    /// not worth a retry.
    /// </remarks>
    Task IncrementViewCountAsync(string listingId, CancellationToken ct);

    /// <summary>
    /// The seller's handle as it was denormalised into the browse projection at publish
    /// time, or <c>null</c> when the listing has never been indexed (a draft, or removed).
    /// </summary>
    /// <remarks>
    /// <see cref="Listing"/> itself carries only <see cref="Listing.SellerId"/>, the
    /// Cognito subject — usernames live in Cognito, and there is no admin lookup wired
    /// from an arbitrary user id back to one. The browse projection already carries the
    /// username for every publicly visible listing, so this reads that row instead of
    /// standing up a second subsystem to answer the same question.
    /// </remarks>
    /// <remarks>
    /// Legacy and <b>not for display</b>: for an email account the stored value is the email
    /// address. Public and counter-party-facing names come from <c>PublicIdentityResolver</c>.
    /// </remarks>
    Task<string?> FindSellerUsernameAsync(string listingId, CancellationToken ct);

    /// <summary>
    /// How many of a seller's listings currently have <paramref name="status"/>. A count query on
    /// the seller index (a key-condition prefix), never a read of the listings themselves.
    /// </summary>
    Task<int> CountBySellerAsync(string sellerId, ListingStatus status, CancellationToken ct);

    /// <summary>
    /// The owning seller of each listing id, in one round trip. Ids that do not exist are absent.
    /// </summary>
    /// <remarks>
    /// Serves browse rows written before the projection carried a seller id, so their stored
    /// (possibly email-shaped) name can be replaced by the seller's public handle.
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> FindSellerIdsAsync(
        IReadOnlyCollection<string> listingIds, CancellationToken ct);
}

/// <summary>Reads and writes the one profile document each account holds.</summary>
public interface IProfileRepository
{
    Task<UserProfile?> FindAsync(string userId, CancellationToken ct);

    /// <summary>Adds a profile, failing if the account already has one.</summary>
    Task AddAsync(UserProfile profile, CancellationToken ct);

    /// <summary>Saves a profile, failing if it changed since it was read.</summary>
    /// <exception cref="ConcurrencyConflictException">The profile changed concurrently.</exception>
    Task UpdateAsync(UserProfile profile, long expectedVersion, CancellationToken ct);
}

/// <summary>Reads categories and their filter schemas.</summary>
/// <remarks>
/// Implementations cache aggressively — categories change roughly monthly and are read
/// on nearly every request.
/// </remarks>
public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken ct);

    Task<Category?> FindByIdAsync(string categoryId, CancellationToken ct);

    Task<Category?> FindBySlugAsync(string slug, CancellationToken ct);
}

/// <summary>Queries the browse/search projection.</summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="IListingRepository"/> because it answers a different
/// question against different storage. The repository serves "give me this listing by
/// id" from the single DynamoDB table; this serves "give me page 3 of PSA 10 cards in
/// Bandung under Rp5.000.000, with a count for every facet option" from a projection
/// kept in step with the table.
/// </para>
/// <para>
/// The first implementation projects into DynamoDB with maintained facet counters,
/// which covers the launch catalogue. The interface is shaped so an OpenSearch-backed
/// implementation can replace it without touching a handler.
/// </para>
/// </remarks>
public interface IListingSearchIndex
{
    Task<NumberedPage<ListingSummary>> SearchAsync(BrowseQuery query, CancellationToken ct);

    /// <summary>Counts per facet option for the current result set, for the sidebar.</summary>
    Task<IReadOnlyList<FacetCount>> CountFacetsAsync(BrowseQuery query, CancellationToken ct);

    /// <summary>Adds or refreshes a listing's entry. Called after any listing write.</summary>
    Task IndexAsync(ListingIndexEntry entry, CancellationToken ct);

    /// <summary>
    /// Updates only the visible price and bid count of an already-indexed auction listing.
    /// </summary>
    /// <remarks>
    /// A narrower write than <see cref="IndexAsync"/> on purpose: bidding is the hottest
    /// write path in the product, and re-deriving the seller's username, photos, and every
    /// facet on every bid just to move one number would cost far more than the bid itself.
    /// A no-op if the listing is not currently indexed (not yet published, or removed) —
    /// there is nothing here for a bid to have caught up with.
    /// </remarks>
    Task UpdatePriceAsync(string listingId, long currentPriceRupiah, int bidCount, CancellationToken ct);

    /// <summary>Removes a listing from browse results.</summary>
    Task RemoveAsync(string listingId, CancellationToken ct);
}

/// <summary>Reads and writes auctions and their bid history.</summary>
public interface IAuctionRepository
{
    Task<Auction?> FindAsync(string listingId, CancellationToken ct);

    Task AddAsync(Auction auction, CancellationToken ct);

    /// <summary>
    /// Persists the auction and appends bid rows in a single transaction.
    /// </summary>
    /// <remarks>
    /// One transaction, conditioned on <paramref name="expectedVersion"/>. A bid that
    /// updated the price but failed to record its history row would leave an auction
    /// whose current price nobody can account for — the exact thing bidders dispute.
    /// </remarks>
    /// <exception cref="ConcurrencyConflictException">Another bid landed first.</exception>
    Task SaveBidAsync(Auction auction, IReadOnlyList<Bid> newBids, long expectedVersion, CancellationToken ct);

    /// <summary>Bid history, newest first.</summary>
    Task<IReadOnlyList<Bid>> GetBidHistoryAsync(string listingId, int limit, CancellationToken ct);

    /// <summary>Auctions past their end time that have not been closed yet.</summary>
    Task<IReadOnlyList<Auction>> ListDueForCloseAsync(DateTimeOffset asOf, int limit, CancellationToken ct);

    /// <summary>
    /// Ended auctions whose winner's thirty minutes to pay are up and not yet settled, longest overdue first.
    /// Same sparse index and caveats as <see cref="ListDueForCloseAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Auction>> ListDueForWinnerPaymentAsync(DateTimeOffset asOf, int limit, CancellationToken ct);
}

/// <summary>Reads and writes offers.</summary>
public interface IOfferRepository
{
    Task<Offer?> FindAsync(string offerId, CancellationToken ct);

    Task AddAsync(Offer offer, CancellationToken ct);

    Task UpdateAsync(Offer offer, long expectedVersion, CancellationToken ct);

    /// <summary>Offers a seller has received, newest first. Drives the dashboard Offers tab.</summary>
    Task<Page<Offer>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct);

    /// <summary>Offers a buyer has sent. Drives the "My offers" tab.</summary>
    Task<Page<Offer>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct);

    /// <summary>An existing unresolved offer from this buyer on this listing, if any.</summary>
    /// <remarks>Used to stop a buyer stacking a dozen simultaneous offers on one item.</remarks>
    Task<Offer?> FindActiveAsync(string listingId, string buyerId, CancellationToken ct);

    /// <summary>
    /// Every offer on this listing that still occupies its buyer's one open slot (pending,
    /// countered, accepted, or checked out), as stored: the deadline is not applied.
    /// </summary>
    /// <remarks>
    /// A Query on the listing's own partition (the active-offer pointer rows), never a scan. Used to
    /// refuse delisting an item with an accepted deal in flight.
    /// </remarks>
    Task<IReadOnlyList<Offer>> ListOpenForListingAsync(string listingId, CancellationToken ct);
}

/// <summary>Reads and writes orders.</summary>
public interface IOrderRepository
{
    Task<Order?> FindAsync(string orderId, CancellationToken ct);

    /// <summary>Looks an order up by the reference printed on receipts.</summary>
    Task<Order?> FindByOrderNumberAsync(string orderNumber, CancellationToken ct);

    /// <summary>Persists several orders from one checkout atomically.</summary>
    /// <remarks>
    /// A checkout spanning three shops must not half-succeed: the buyer would be shown
    /// one payment covering orders that do not all exist.
    /// </remarks>
    Task AddManyAsync(IReadOnlyList<Order> orders, CancellationToken ct);

    Task UpdateAsync(Order order, long expectedVersion, CancellationToken ct);

    /// <summary>
    /// Saves a just-released order and the payout it owes in one transaction: both are written or neither,
    /// so escrow is never released without the seller being owed.
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The order changed since it was read, or its payout already exists.</exception>
    Task UpdateWithPayoutAsync(Order order, long expectedVersion, Payout payout, CancellationToken ct);

    Task<Page<Order>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct);

    Task<Page<Order>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct);

    /// <summary>Delivered orders whose auto-release deadline has passed, most overdue first.</summary>
    /// <remarks>
    /// Answered from a sparse index, so only orders written with their sweep keys are found:
    /// rows older than that index are never returned (see the sweep's backfill note).
    /// </remarks>
    Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(DateTimeOffset asOf, int limit, CancellationToken ct);

    /// <summary>Unpaid orders whose payment window has elapsed, most overdue first. Same caveat as above.</summary>
    Task<IReadOnlyList<Order>> ListPaymentExpiredAsync(DateTimeOffset asOf, int limit, CancellationToken ct);

    /// <summary>
    /// Shipped orders whose parcel is due to be asked about (<see cref="Order.NextTrackingCheckAt"/> has
    /// passed), longest-waiting first. Same sparse index and caveat as above.
    /// </summary>
    Task<IReadOnlyList<Order>> ListDueForTrackingAsync(DateTimeOffset asOf, int limit, CancellationToken ct);
}

/// <summary>Reads and writes a buyer's cart — the listings they intend to check out together.</summary>
/// <remarks>
/// Deliberately thin: a cart entry is a pointer to a listing, not a priced snapshot. The
/// price, photo, and availability shown on the cart screen are read live from
/// <see cref="IListingRepository"/> at cart-read time rather than frozen at the moment the
/// item was added, so a seller's price change or sale elsewhere is reflected immediately.
/// </remarks>
public interface ICartRepository
{
    /// <summary>Adds a listing, or refreshes it if already present (the quantity is replaced, not added to).</summary>
    Task AddItemAsync(string buyerId, string listingId, DateTimeOffset addedAt, CancellationToken ct, int quantity = 1);

    /// <summary>Removes a listing. A no-op if it was not there.</summary>
    Task RemoveItemAsync(string buyerId, string listingId, CancellationToken ct);

    /// <summary>Every listing currently in the buyer's cart.</summary>
    Task<IReadOnlyList<CartItem>> ListAsync(string buyerId, CancellationToken ct);
}

/// <summary>One pointer row in a buyer's cart.</summary>
public sealed record CartItem(string ListingId, DateTimeOffset AddedAt, int Quantity = 1);

/// <summary>Everything the browse projection needs to render one result card.</summary>
/// <param name="Listing">The listing itself.</param>
/// <param name="SellerUsername">
/// The seller's handle. Denormalised into the projection because a result grid shows
/// twelve sellers and must not cost twelve extra reads.
/// </param>
/// <param name="AuctionEndsAt">Close time when this is a live auction, otherwise <c>null</c>.</param>
/// <param name="BidCount">Bids so far, for the auction card's "14 bids" line.</param>
/// <remarks>
/// Passed explicitly rather than looked up inside the index. Two of these three values
/// belong to other aggregates, and an index that quietly re-reads them would turn one
/// listing write into three round trips whose cost is invisible at the call site.
/// </remarks>
public sealed record ListingIndexEntry(
    Listing Listing,
    string SellerUsername,
    DateTimeOffset? AuctionEndsAt,
    int BidCount);

/// <summary>A listing as it appears in a grid or list row.</summary>
/// <remarks>
/// Deliberately narrower than <see cref="Listing"/>. Browse renders twelve of these per
/// page and needs none of the description, so the projection stores only what a card
/// draws — which also means a field added to the aggregate cannot accidentally start
/// being served to anonymous visitors.
/// </remarks>
public sealed record ListingSummary(
    string Id,
    string Title,
    string CategoryId,
    string CategorySlug,
    long PriceRupiah,
    ListingFormat Format,
    ListingStatus Status,
    string Condition,
    string SellerUsername,
    string City,
    string? PrimaryPhotoKey,
    DateTimeOffset? AuctionEndsAt,
    int BidCount,
    [property: JsonIgnore] string? SellerId = null);

/// <summary>The count for one facet option in the current result set.</summary>
public sealed record FacetCount(string FacetKey, string Value, long Count);

/// <summary>A browse or search request, as parsed from the query string.</summary>
/// <param name="CategoryId">Category to browse, or <c>null</c> to search everything.</param>
/// <param name="SearchTerm">Free-text query from the header search box.</param>
/// <param name="SelectedFacets">Chosen facet values, keyed by facet id.</param>
/// <param name="MinPriceRupiah">Inclusive lower price bound.</param>
/// <param name="MaxPriceRupiah">Inclusive upper price bound.</param>
/// <param name="City">Seller city filter.</param>
/// <param name="Sort">Result ordering.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="PageSize">Results per page.</param>
public sealed record BrowseQuery(
    string? CategoryId,
    string? SearchTerm,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SelectedFacets,
    long? MinPriceRupiah,
    long? MaxPriceRupiah,
    string? City,
    BrowseSort Sort,
    int PageNumber,
    int PageSize)
{
    /// <summary>Matches the design's 12-per-page browse grid.</summary>
    public const int DefaultPageSize = 12;

    /// <summary>
    /// Hard ceiling on page size.
    /// </summary>
    /// <remarks>
    /// Without a cap, <c>?pageSize=100000</c> is a free denial-of-service primitive
    /// against both our read capacity and our egress bill.
    /// </remarks>
    public const int MaxPageSize = 48;

    /// <summary>
    /// Hard ceiling on page number.
    /// </summary>
    /// <remarks>
    /// Deep paging costs grow with depth, and no human browses to page 900. Crawlers
    /// will happily try; the frontend emits <c>rel="next"</c> only within this bound.
    /// </remarks>
    public const int MaxPageNumber = 200;
}

/// <summary>Result ordering offered by the browse sort control.</summary>
public enum BrowseSort
{
    Relevance = 0,
    Newest = 1,
    PriceAscending = 2,
    PriceDescending = 3,
    AuctionsEndingSoon = 4,
}
