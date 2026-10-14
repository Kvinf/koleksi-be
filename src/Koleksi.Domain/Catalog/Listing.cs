using Koleksi.Domain.Common;

namespace Koleksi.Domain.Catalog;

/// <summary>
/// A single item offered for sale — the aggregate root behind the item page, the
/// browse grid, and the seller dashboard's Listings tab.
/// </summary>
/// <remarks>
/// <para>
/// Auctions keep their bidding state in a separate <c>Auction</c> aggregate keyed by
/// the same id. A listing with <see cref="ListingFormat.Auction"/> is the descriptive
/// half (title, photos, category); the auction is the transactional half (current bid,
/// bid history, close time). They are split because bids are written far more often
/// than descriptions, and DynamoDB conditional writes on the auction record must not
/// contend with a seller editing their copy.
/// </para>
/// <para>
/// All state changes go through methods on this type. Setters are private so a
/// listing can never be left in a shape the domain forbids — an active listing with
/// no photos, or a price of zero.
/// </para>
/// </remarks>
public sealed class Listing
{
    public const int MinTitleLength = 10;
    public const int MaxTitleLength = 140;
    public const int MaxDescriptionLength = 4000;
    public const int MaxPhotos = 8;
    public const int MinPhotosToPublish = 1;

    /// <summary>
    /// The floor for any listed price.
    /// </summary>
    /// <remarks>
    /// Below roughly Rp10.000 the payment processor's fixed fee exceeds the item's
    /// value, which produces negative-margin orders and is a well-worn vector for
    /// card-testing fraud. Rejecting them at the domain boundary is cheaper than
    /// reconciling them later.
    /// </remarks>
    public static readonly Money MinimumPrice = Money.FromRupiah(10_000);

    /// <summary>
    /// The ceiling for any listed price: Rp 1.000.000.000 per item (QA D-07).
    /// </summary>
    /// <remarks>
    /// Keeps every total (an order of up to 20 items plus shipping) far inside a <see cref="long"/>,
    /// and the price sort key (16 digits, see <c>DynamoTable</c>) well inside its width, so a huge
    /// price can neither overflow arithmetic nor sort out of order. Above this is not a C2C
    /// collectible sale this escrow is built for.
    /// </remarks>
    public static readonly Money MaximumPrice = Money.FromRupiah(1_000_000_000);

    /// <summary>The most units one listing can offer.</summary>
    public const int MaxStock = 99;

    /// <summary>Longest condition label accepted; it rides on every browse card.</summary>
    public const int MaxConditionLength = 60;

    /// <summary>Longest city accepted; it rides on every browse card.</summary>
    public const int MaxCityLength = 80;

    private readonly List<ListingPhoto> _photos = [];
    private readonly Dictionary<string, string> _attributes = new(StringComparer.Ordinal);

    private Listing(
        string id,
        string sellerId,
        string categoryId,
        string title,
        Money price,
        ListingFormat format,
        string condition,
        string city,
        DateTimeOffset createdAt)
    {
        Id = id;
        SellerId = sellerId;
        CategoryId = categoryId;
        Title = title;
        Price = price;
        Format = format;
        Condition = condition;
        City = city;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Status = ListingStatus.Draft;
    }

    public string Id { get; }

    /// <summary>The Cognito subject of the seller. The only identity permitted to mutate this listing.</summary>
    public string SellerId { get; }

    public string CategoryId { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public Money Price { get; private set; }

    /// <summary>
    /// How many units the seller is offering at this price: 1 for a one-of-one collectible, more for
    /// something they have several of. An auction is always exactly one. How many are still free is not
    /// stored here: it is what the reservation store says, because units are taken by orders that may
    /// still lapse (see <c>IListingHoldStore</c>).
    /// </summary>
    public int Stock { get; private set; } = 1;

    public ListingFormat Format { get; private set; }
    public ListingStatus Status { get; private set; }

    /// <summary>Condition label as chosen from the category's vocabulary — <c>Mint</c>, <c>PSA 10</c>, <c>BNIB</c>.</summary>
    public string Condition { get; private set; }

    /// <summary>Origin city, used for the <c>seller · city</c> line and shipping estimates.</summary>
    public string City { get; private set; }

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Ordered photos; the first is the grid thumbnail.</summary>
    public IReadOnlyList<ListingPhoto> Photos => _photos;

    /// <summary>
    /// Category-specific facet values, keyed by facet id — <c>grade</c> → <c>PSA 10</c>,
    /// <c>size_eu</c> → <c>42</c>. Drives the browse filters, which differ per category.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    /// <summary>
    /// Denormalised counters shown on the item page and dashboard.
    /// </summary>
    /// <remarks>
    /// These are advisory. They are incremented with unconditional DynamoDB atomic
    /// counters rather than read-modify-write, so they never block a page render and
    /// never cause a write conflict. A lost view count is not worth a retry.
    /// </remarks>
    public long ViewCount { get; private set; }

    public long WatcherCount { get; private set; }

    /// <summary>
    /// Optimistic-concurrency token. Incremented on every accepted mutation.
    /// </summary>
    /// <remarks>
    /// The persistence layer conditions each update on the value it read. Without this a
    /// seller editing a description in one tab and a price in another silently loses one
    /// of the two edits. Deliberately not <see cref="UpdatedAt"/>: a timestamp used as a
    /// concurrency token fails only when two writes land in the same tick, which is
    /// exactly the case it is meant to catch.
    /// </remarks>
    public long Version { get; private set; }

    /// <summary>Whether buyers may send offers, per the design's <c>nego</c> badge.</summary>
    public bool AcceptsOffers => Format == ListingFormat.AcceptOffers;

    /// <summary>
    /// Offers below this share of the asking price are auto-declined, matching the
    /// sell form's "auto-decline under 70%" control. <c>null</c> means no auto-decline.
    /// </summary>
    public decimal? AutoDeclineBelowPercent { get; private set; }

    /// <summary>
    /// How long the auction runs once published, in days. Kept on the listing so a draft
    /// remembers it: the <see cref="Koleksi.Domain.Auctions.Auction"/> itself does not exist
    /// until the listing is published. <c>null</c> for every other format, and for drafts
    /// saved before this was stored.
    /// </summary>
    public int? AuctionDurationDays { get; private set; }

    /// <summary>The reserve the auction will be created with. <c>null</c> means any bid wins.</summary>
    public Money? AuctionReservePrice { get; private set; }

    /// <summary>The buy-now price the auction will be created with. <c>null</c> means none.</summary>
    public Money? AuctionBuyoutPrice { get; private set; }

    /// <summary>The public URL path for this listing, e.g. <c>/kartu/psa-10-charizard-01J8Z...</c>.</summary>
    public string UrlPath(string categorySlug) => $"/{categorySlug}/{Slug.From(Title)}-{Id}";

    /// <summary>Whether the listing is visible to an anonymous visitor.</summary>
    public bool IsPubliclyVisible => Status is ListingStatus.Active or ListingStatus.Sold;

    /// <summary>Creates a draft listing. Nothing is visible to buyers until <see cref="Publish"/>.</summary>
    /// <remarks>
    /// A draft is a work in progress, so only what identifies it is required: a category and a
    /// non-blank title. The price, condition, city, photos and description may all still be
    /// empty, and the title may still be shorter than a published one must be. Every one of
    /// those is enforced when the listing goes live, in <see cref="Publish"/>.
    /// </remarks>
    public static Listing CreateDraft(
        string sellerId,
        string categoryId,
        string title,
        Money price,
        ListingFormat format,
        string condition,
        string city,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;
        return new Listing(
            EntityId.New(now),
            sellerId,
            categoryId,
            ValidateDraftTitle(title),
            EnsureWithinCeiling(price),
            format,
            BoundedLabel(condition, MaxConditionLength, "listing.condition_too_long", "Condition"),
            BoundedLabel(city, MaxCityLength, "listing.city_too_long", "City"),
            now);
    }

    /// <summary>
    /// Reconstructs a listing from storage without re-running creation rules.
    /// </summary>
    /// <remarks>
    /// Persisted rows predate today's rules and must load even when a validation
    /// threshold has since tightened; refusing to rehydrate would make stored data
    /// unreadable. Only the persistence layer may call this.
    /// </remarks>
    public static Listing Rehydrate(
        string id,
        string sellerId,
        string categoryId,
        string title,
        string description,
        Money price,
        ListingFormat format,
        ListingStatus status,
        string condition,
        string city,
        IEnumerable<ListingPhoto> photos,
        IReadOnlyDictionary<string, string> attributes,
        long viewCount,
        long watcherCount,
        decimal? autoDeclineBelowPercent,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? publishedAt,
        long version,
        int? auctionDurationDays = null,
        Money? auctionReservePrice = null,
        Money? auctionBuyoutPrice = null,
        int stock = 1)
    {
        var listing = new Listing(id, sellerId, categoryId, title, price, format, condition, city, createdAt)
        {
            // Rows written before stock existed are one-of-one.
            Stock = format == ListingFormat.Auction ? 1 : Math.Clamp(stock, 1, MaxStock),
            Description = description,
            Status = status,
            ViewCount = viewCount,
            WatcherCount = watcherCount,
            AutoDeclineBelowPercent = autoDeclineBelowPercent,
            AuctionDurationDays = auctionDurationDays,
            AuctionReservePrice = auctionReservePrice,
            AuctionBuyoutPrice = auctionBuyoutPrice,
            UpdatedAt = updatedAt,
            PublishedAt = publishedAt,
            Version = version,
        };

        listing._photos.AddRange(photos);
        foreach (var (key, value) in attributes)
        {
            listing._attributes[key] = value;
        }

        return listing;
    }

    public void SetDescription(string description, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.Length > MaxDescriptionLength)
        {
            throw DomainException.For(
                "listing.description_too_long",
                $"Description exceeds {MaxDescriptionLength} characters.");
        }

        Description = description.Trim();
        Touch(clock);
    }

    public void SetPhotos(IEnumerable<ListingPhoto> photos, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(photos);

        var incoming = photos.ToList();
        if (incoming.Count > MaxPhotos)
        {
            throw DomainException.For("listing.too_many_photos", $"A listing accepts at most {MaxPhotos} photos.");
        }

        _photos.Clear();
        _photos.AddRange(incoming);
        Touch(clock);
    }

    /// <summary>Replaces the category facet values, e.g. grade and set for a trading card.</summary>
    public void SetAttributes(IReadOnlyDictionary<string, string> attributes, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        _attributes.Clear();
        foreach (var (key, value) in attributes)
        {
            _attributes[key] = value;
        }

        Touch(clock);
    }

    /// <summary>Sets the auto-decline threshold for offers, or clears it when <c>null</c>.</summary>
    public void SetAutoDeclineThreshold(decimal? percentOfAsking, IClock clock)
    {
        if (percentOfAsking is { } percent && percent is < 1m or > 100m)
        {
            throw DomainException.For(
                "listing.invalid_auto_decline",
                "Auto-decline threshold must be between 1% and 100% of the asking price.");
        }

        AutoDeclineBelowPercent = percentOfAsking;
        Touch(clock);
    }

    /// <summary>
    /// Remembers the auction terms the seller chose, so saving a draft does not lose them.
    /// Cleared (all <c>null</c>) for any other format.
    /// </summary>
    /// <remarks>
    /// Stored as given: whether the terms are <em>valid</em> (a duration in range, a reserve
    /// between the start and the buyout) is the auction's own rule and is enforced when the
    /// auction is created at publish, so a half-finished draft can still be saved.
    /// </remarks>
    public void SetAuctionTerms(int? durationDays, Money? reservePrice, Money? buyoutPrice, IClock clock)
    {
        if (durationDays is <= 0)
        {
            throw DomainException.For("auction.invalid_duration", "Auction duration must be at least one day.");
        }

        AuctionDurationDays = durationDays;
        AuctionReservePrice = reservePrice is { } reserve ? EnsureWithinCeiling(reserve) : null;
        AuctionBuyoutPrice = buyoutPrice is { } buyout ? EnsureWithinCeiling(buyout) : null;
        Touch(clock);
    }

    /// <summary>Makes the listing visible to buyers.</summary>
    /// <exception cref="DomainException">The listing is incomplete or not in a publishable state.</exception>
    public void Publish(IClock clock)
    {
        if (Status is not (ListingStatus.Draft or ListingStatus.Archived))
        {
            throw DomainException.For("listing.not_publishable", $"A {Status} listing cannot be published.");
        }

        EnsureComplete();

        Status = ListingStatus.Active;
        PublishedAt ??= clock.UtcNow;
        Touch(clock);
    }

    /// <summary>
    /// Sets how many units are on offer. An auction is always one. Refused once the listing is sold or
    /// suspended. The caller is responsible for checking the new number against units already sold or
    /// reserved (only the reservation store knows them).
    /// </summary>
    /// <exception cref="DomainException">The number is outside 1 to <see cref="MaxStock"/>, or the listing cannot change.</exception>
    public void SetStock(int stock, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status is ListingStatus.Sold or ListingStatus.Suspended)
        {
            throw DomainException.For("listing.not_editable", $"A {Status} listing cannot be edited.");
        }

        if (stock is < 1 or > MaxStock)
        {
            throw DomainException.For("listing.invalid_stock", $"Stock must be between 1 and {MaxStock}.");
        }

        if (Format == ListingFormat.Auction && stock != 1)
        {
            throw DomainException.For("listing.auction_stock_fixed", "An auction is always a single item.");
        }

        if (stock == Stock)
        {
            return;
        }

        Stock = stock;
        Touch(clock);
    }

    /// <summary>Withdraws the listing from browse. Reversible by the seller.</summary>
    public void Archive(IClock clock)
    {
        if (Status == ListingStatus.Sold)
        {
            throw DomainException.For("listing.already_sold", "A sold listing cannot be archived.");
        }

        if (Status == ListingStatus.Suspended)
        {
            throw DomainException.For("listing.suspended", "A suspended listing cannot be modified.");
        }

        Status = ListingStatus.Archived;
        Touch(clock);
    }

    /// <summary>
    /// Marks the listing sold once an order has been paid for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only from the order flow, after payment is confirmed by the gateway
    /// webhook — never from a client request. This is what stops a second buyer from
    /// purchasing an item that is already gone.
    /// </para>
    /// <para>
    /// An <see cref="ListingStatus.Archived"/> listing can still be sold: payment is authoritative.
    /// The money has been taken for this item, so a seller who delisted it while the buyer was on
    /// the payment page must not be left holding a Paid order for a listing they can republish and
    /// sell a second time. Archiving is refused while a sale is in flight (see the archive
    /// endpoint), so this only covers a payment that lands after its hold lapsed.
    /// </para>
    /// </remarks>
    public void MarkSold(IClock clock)
    {
        if (Status is not (ListingStatus.Active or ListingStatus.Archived))
        {
            throw DomainException.For("listing.not_purchasable", $"A {Status} listing cannot be sold.");
        }

        Status = ListingStatus.Sold;
        Touch(clock);
    }

    /// <summary>Removes the listing from sale by moderator action. Not reversible by the seller.</summary>
    public void Suspend(IClock clock)
    {
        Status = ListingStatus.Suspended;
        Touch(clock);
    }

    /// <summary>
    /// Replaces every seller-editable field on a listing that has never been live — same shape as
    /// <see cref="CreateDraft"/>, since editing a draft is filling in the same form again.
    /// </summary>
    /// <remarks>
    /// Restricted to <see cref="IsUnpublished"/> deliberately: once a listing has been live,
    /// changing its category, format or title would misdescribe something a buyer may already be
    /// watching or bidding on. A listing that is or was live is edited with <see cref="EditLive"/>
    /// instead, which leaves those fields alone. Draft rules apply here, not publish rules (see
    /// <see cref="CreateDraft"/>); <see cref="Publish"/> checks the rest.
    /// </remarks>
    /// <exception cref="DomainException">The listing is, or has been, live.</exception>
    public void EditDraft(
        string categoryId,
        string title,
        Money price,
        ListingFormat format,
        string condition,
        string city,
        IClock clock)
    {
        if (!IsUnpublished)
        {
            throw DomainException.For("listing.not_editable", $"A {Status} listing cannot be edited this way.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);

        CategoryId = categoryId;
        Title = ValidateDraftTitle(title);
        Price = EnsureWithinCeiling(price);
        Format = format;
        if (format == ListingFormat.Auction)
        {
            Stock = 1;
        }

        Condition = BoundedLabel(condition, MaxConditionLength, "listing.condition_too_long", "Condition");
        City = BoundedLabel(city, MaxCityLength, "listing.city_too_long", "City");
        Touch(clock);
    }

    /// <summary>
    /// Edits a listing that is, or has been, live: the fields a seller may safely correct after
    /// buyers can see it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Editable: description, photos, condition, city, and the price of a fixed-price or nego
    /// listing. Never editable here: seller, category, format, title, and (for an auction) the
    /// starting price and terms. The title stays fixed because it is what a watcher, offerer or
    /// bidder agreed to; a typo is fixed by delisting and relisting, not silently under their feet.
    /// </para>
    /// <para>
    /// An auction is editable only while it has no bids, the same line <c>Auction.Cancel</c> draws
    /// (<c>auction.has_bids</c>): a bidder has committed money to what the page said at the time.
    /// The domain cannot see bids (they live on the separate Auction aggregate), so the caller
    /// passes whether any exist.
    /// </para>
    /// <para>
    /// Everything is validated before anything is assigned, and the version moves once, so a
    /// refused edit leaves the listing exactly as it was and an accepted one is a single change.
    /// </para>
    /// </remarks>
    /// <param name="auctionHasBids">Whether this listing's auction has taken at least one bid. Ignored for other formats.</param>
    /// <exception cref="DomainException">The listing is not editable, or a value breaks a listing rule.</exception>
    public void EditLive(
        Money price,
        string description,
        string condition,
        string city,
        IEnumerable<ListingPhoto> photos,
        bool auctionHasBids,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(clock);

        if (IsUnpublished || Status is not (ListingStatus.Active or ListingStatus.Archived))
        {
            throw DomainException.For("listing.not_editable", $"A {Status} listing cannot be edited.");
        }

        if (Format == ListingFormat.Auction)
        {
            if (auctionHasBids)
            {
                throw DomainException.For(
                    "auction.has_bids",
                    "An auction that already has bids cannot be edited.");
            }

            if (price != Price)
            {
                throw DomainException.For(
                    "listing.auction_price_immutable",
                    "An auction's starting bid cannot be changed once created.");
            }
        }
        else if (price != Price)
        {
            // An unchanged price is never re-checked: a stored price from before the floor existed
            // must not make an unrelated description edit fail.
            ValidatePrice(price);
        }

        var newPhotos = photos.ToList();
        if (newPhotos.Count < MinPhotosToPublish)
        {
            throw DomainException.For("listing.photo_required", "A live listing needs at least one photo.");
        }

        if (newPhotos.Count > MaxPhotos)
        {
            throw DomainException.For("listing.too_many_photos", $"A listing accepts at most {MaxPhotos} photos.");
        }

        if (description.Length > MaxDescriptionLength)
        {
            throw DomainException.For(
                "listing.description_too_long",
                $"Description exceeds {MaxDescriptionLength} characters.");
        }

        var trimmedCondition = BoundedLabel(condition, MaxConditionLength, "listing.condition_too_long", "Condition");
        if (trimmedCondition.Length == 0)
        {
            throw DomainException.For("listing.condition_required", "Choose the item's condition.");
        }

        var trimmedCity = BoundedLabel(city, MaxCityLength, "listing.city_too_long", "City");
        if (trimmedCity.Length == 0)
        {
            throw DomainException.For("listing.city_required", "Enter the city the item ships from.");
        }

        Price = price;
        Description = description.Trim();
        Condition = trimmedCondition;
        City = trimmedCity;
        _photos.Clear();
        _photos.AddRange(newPhotos);
        Touch(clock);
    }

    public void ChangePrice(Money price, IClock clock)
    {
        if (Format == ListingFormat.Auction)
        {
            throw DomainException.For(
                "listing.auction_price_immutable",
                "An auction's starting bid cannot be changed once created.");
        }

        if (Status == ListingStatus.Sold)
        {
            throw DomainException.For("listing.already_sold", "A sold listing's price cannot be changed.");
        }

        Price = ValidatePrice(price);
        Touch(clock);
    }

    /// <summary>Confirms that <paramref name="userId"/> owns this listing.</summary>
    /// <exception cref="DomainException">The caller is not the seller.</exception>
    public void EnsureOwnedBy(string userId)
    {
        // Ordinal comparison: Cognito subjects are opaque identifiers, and a
        // culture-sensitive comparison could treat distinct subjects as equal.
        if (!string.Equals(SellerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("listing.forbidden", "This listing belongs to another seller.");
        }
    }

    /// <summary>Applies a view-count delta read back from the atomic counter.</summary>
    public void ApplyViewCount(long value) => ViewCount = value;

    /// <summary>Applies a watcher-count delta read back from the atomic counter.</summary>
    public void ApplyWatcherCount(long value) => WatcherCount = value;

    /// <summary>
    /// True until the listing has gone live for the first time: a draft, or a draft that was
    /// delisted before ever being published. Such a listing has no buyers to mislead, so it is
    /// edited as freely as a draft.
    /// </summary>
    public bool IsUnpublished =>
        Status == ListingStatus.Draft || (Status == ListingStatus.Archived && PublishedAt is null);

    /// <summary>
    /// The rules a listing must meet to be shown to buyers. A draft may break all of them; this
    /// is the single place they are enforced.
    /// </summary>
    private void EnsureComplete()
    {
        ValidateTitle(Title);
        ValidatePrice(Price);

        if (string.IsNullOrWhiteSpace(Condition))
        {
            throw DomainException.For("listing.condition_required", "Choose the item's condition before publishing.");
        }

        if (string.IsNullOrWhiteSpace(City))
        {
            throw DomainException.For("listing.city_required", "Enter the city the item ships from before publishing.");
        }

        if (_photos.Count < MinPhotosToPublish)
        {
            throw DomainException.For("listing.photo_required", "Add at least one photo before publishing.");
        }
    }

    // A draft needs a title to be told apart from its siblings on the dashboard, but not the
    // length a public title must have. The length floor is checked at publish (ValidateTitle).
    private static string ValidateDraftTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var trimmed = title.Trim();
        return trimmed.Length > MaxTitleLength
            ? throw DomainException.For(
                "listing.title_too_long",
                $"Title must be at most {MaxTitleLength} characters.")
            : trimmed;
    }

    private static string ValidateTitle(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var trimmed = title.Trim();
        return trimmed.Length switch
        {
            < MinTitleLength => throw DomainException.For(
                "listing.title_too_short",
                $"Title must be at least {MinTitleLength} characters."),
            > MaxTitleLength => throw DomainException.For(
                "listing.title_too_long",
                $"Title must be at most {MaxTitleLength} characters."),
            _ => trimmed,
        };
    }

    private static Money ValidatePrice(Money price) => price < MinimumPrice
        ? throw DomainException.For("listing.price_too_low", $"Price must be at least {MinimumPrice}.")
        : EnsureWithinCeiling(price);

    /// <summary>The ceiling alone, for drafts, which may still be below the floor.</summary>
    private static Money EnsureWithinCeiling(Money price) => price > MaximumPrice
        ? throw DomainException.For("listing.price_too_high", $"Price must be at most {MaximumPrice}.")
        : price;

    private static string BoundedLabel(string? value, int maxLength, string code, string name)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > maxLength
            ? throw DomainException.For(code, $"{name} must be at most {maxLength} characters.")
            : trimmed;
    }

    private void Touch(IClock clock)
    {
        UpdatedAt = clock.UtcNow;
        Version++;
    }
}
