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

    /// <summary>The public URL path for this listing, e.g. <c>/kartu/psa-10-charizard-01J8Z...</c>.</summary>
    public string UrlPath(string categorySlug) => $"/{categorySlug}/{Slug.From(Title)}-{Id}";

    /// <summary>Whether the listing is visible to an anonymous visitor.</summary>
    public bool IsPubliclyVisible => Status is ListingStatus.Active or ListingStatus.Sold;

    /// <summary>Creates a draft listing. Nothing is visible to buyers until <see cref="Publish"/>.</summary>
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
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        ArgumentException.ThrowIfNullOrWhiteSpace(city);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.UtcNow;
        return new Listing(
            EntityId.New(now),
            sellerId,
            categoryId,
            ValidateTitle(title),
            ValidatePrice(price),
            format,
            condition.Trim(),
            city.Trim(),
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
        long version)
    {
        var listing = new Listing(id, sellerId, categoryId, title, price, format, condition, city, createdAt)
        {
            Description = description,
            Status = status,
            ViewCount = viewCount,
            WatcherCount = watcherCount,
            AutoDeclineBelowPercent = autoDeclineBelowPercent,
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

    /// <summary>Makes the listing visible to buyers.</summary>
    /// <exception cref="DomainException">The listing is incomplete or not in a publishable state.</exception>
    public void Publish(IClock clock)
    {
        if (Status is not (ListingStatus.Draft or ListingStatus.Archived))
        {
            throw DomainException.For("listing.not_publishable", $"A {Status} listing cannot be published.");
        }

        if (_photos.Count < MinPhotosToPublish)
        {
            throw DomainException.For("listing.photo_required", "Add at least one photo before publishing.");
        }

        Status = ListingStatus.Active;
        PublishedAt ??= clock.UtcNow;
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
    /// Called only from the order flow, after payment is confirmed by the gateway
    /// webhook — never from a client request. This is what stops a second buyer from
    /// purchasing an item that is already gone.
    /// </remarks>
    public void MarkSold(IClock clock)
    {
        if (Status != ListingStatus.Active)
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
        : price;

    private void Touch(IClock clock)
    {
        UpdatedAt = clock.UtcNow;
        Version++;
    }
}
