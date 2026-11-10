using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;

namespace Koleksi.Domain.Bounties;

/// <summary>Where a Dicari stands.</summary>
public enum BountyStatus
{
    /// <summary>Visible on the board and, unless a deal is in flight, taking proposals.</summary>
    Open = 0,

    /// <summary>An order made from an accepted proposal was paid. Terminal.</summary>
    Fulfilled = 1,

    /// <summary>Closed by the buyer. Terminal.</summary>
    Closed = 2,

    /// <summary>Ran past <see cref="Bounty.ExpiresAt"/> with no paid deal. Terminal.</summary>
    Expired = 3,

    /// <summary>Taken down by staff. Terminal.</summary>
    Removed = 4,
}

/// <summary>The short, quotable reference of a Dicari, <c>DCR-xxxxxx</c>.</summary>
public static class BountyCode
{
    /// <summary>Characters of the bounty id kept in the code (the id's tail is its random part).</summary>
    public const int Length = 6;

    /// <summary>
    /// Derived from the bounty's own id and nothing else, so it is non-sequential (like an order number) and
    /// can never be mistaken for the order's <c>KLK-</c> number, the offer's id or the proposal's id.
    /// </summary>
    public static string For(string bountyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bountyId);
        return bountyId.Length <= Length ? $"DCR-{bountyId}" : $"DCR-{bountyId[^Length..]}";
    }
}

/// <summary>
/// A buyer's "wanted" post: an item they could not find and the most they would pay. Sellers answer with a
/// proposal built on one of their own listings (<see cref="BountyProposal"/>).
/// </summary>
/// <remarks>
/// <para>
/// <c>Open --a proposal is accepted--&gt; Open, deal in flight --its order is paid--&gt; Fulfilled</c>. While a deal is
/// in flight (<see cref="AcceptedProposalId"/> is set) the Dicari takes no new proposals, leaves the board and
/// cannot be closed or expired; if the deal falls through (the offer lapses unpaid) it opens again, or expires if
/// its own deadline has passed meanwhile.
/// </para>
/// <para>
/// <see cref="ProposalCount"/> is maintained by an atomic add in the store, never by this aggregate, so a
/// concurrent edit cannot roll it back. <see cref="NoteProposalAdded"/> mirrors that add in memory.
/// </para>
/// </remarks>
public sealed class Bounty
{
    public const int MinTitleLength = 5;
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 1000;
    public const int MaxPhotos = 3;
    public const int MaxConditions = 8;
    public const int MaxConditionLength = 60;
    public const int MaxCityLength = 80;

    /// <summary>Extra time after an offer's checkout window before an unfinished deal is released, to outlast an order's own payment window.</summary>
    public static readonly TimeSpan DealGrace = TimeSpan.FromHours(1);

    /// <summary>How many Dicari one account may have open at once.</summary>
    public const int MaxOpenPerBuyer = 5;

    /// <summary>The lifetimes a buyer may choose, in days.</summary>
    public static readonly IReadOnlyList<int> AllowedDurationDays = [7, 14, 30];

    private readonly Dictionary<string, string> _facets;
    private readonly List<string> _conditions;
    private readonly List<string> _photoKeys;

    private Bounty(
        string id,
        string buyerId,
        string? buyerUsername,
        string title,
        string categoryId,
        IReadOnlyDictionary<string, string> facets,
        string description,
        IEnumerable<string> conditions,
        Money maxBudget,
        string shipToCity,
        IEnumerable<string> photoKeys,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        BuyerId = buyerId;
        BuyerUsername = buyerUsername;
        Title = title;
        CategoryId = categoryId;
        _facets = new Dictionary<string, string>(facets, StringComparer.Ordinal);
        Description = description;
        _conditions = [.. conditions];
        MaxBudget = maxBudget;
        ShipToCity = shipToCity;
        _photoKeys = [.. photoKeys];
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string Id { get; }

    /// <summary>The short reference, <c>DCR-xxxxxx</c>.</summary>
    public string Code => BountyCode.For(Id);

    public string BuyerId { get; }

    /// <summary>The buyer's public handle when the Dicari was posted.</summary>
    public string? BuyerUsername { get; }

    public string Title { get; private set; }
    public string CategoryId { get; private set; }

    /// <summary>Category facet values, drawn from the same allowlist listings use (checked by the caller against the category).</summary>
    public IReadOnlyDictionary<string, string> Facets => _facets;

    public string Description { get; private set; }

    /// <summary>The conditions the buyer will accept (the same labels listings carry).</summary>
    public IReadOnlyList<string> Conditions => _conditions;

    /// <summary>The most the buyer wants to pay. An upsell may exceed it; accepting one never changes it.</summary>
    public Money MaxBudget { get; private set; }

    /// <summary>City only, copied from the buyer's chosen address. Never the street.</summary>
    public string ShipToCity { get; }

    /// <summary>Up to <see cref="MaxPhotos"/> reference photo base keys under <c>bounties/{ownerId}/{id}/</c>.</summary>
    public IReadOnlyList<string> PhotoKeys => _photoKeys;

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public BountyStatus Status { get; private set; } = BountyStatus.Open;

    /// <summary>How many proposals have been made, whatever became of them.</summary>
    public int ProposalCount { get; private set; }

    /// <summary>The proposal whose offer is awaiting checkout or payment, while a deal is in flight.</summary>
    public string? AcceptedProposalId { get; private set; }

    /// <summary>
    /// When a deal in flight stops being worth waiting for: the offer's checkout window plus <see cref="DealGrace"/>, so an
    /// order paid in the last minutes still lands first. The sweep looks at the Dicari then. <c>null</c> with no deal.
    /// </summary>
    public DateTimeOffset? DealDueAt { get; private set; }

    /// <summary>Why staff removed it, when they did.</summary>
    public string? RemovedReason { get; private set; }

    public long Version { get; private set; }

    public bool DealInFlight => AcceptedProposalId is not null;

    /// <summary>Whether the board shows it and a seller may propose on it right now.</summary>
    public bool AcceptsProposals(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Status == BountyStatus.Open && AcceptedProposalId is null && clock.UtcNow < ExpiresAt;
    }

    /// <summary>The status as the clock sees it: an open Dicari past its deadline with no deal in flight reads as expired.</summary>
    public BountyStatus EffectiveStatus(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Status == BountyStatus.Open && AcceptedProposalId is null && clock.UtcNow >= ExpiresAt
            ? BountyStatus.Expired
            : Status;
    }

    public static Bounty Create(
        string buyerId,
        string? buyerUsername,
        string title,
        string categoryId,
        IReadOnlyDictionary<string, string> facets,
        string? description,
        IEnumerable<string> conditions,
        Money maxBudget,
        string shipToCity,
        IEnumerable<string>? photoKeys,
        int durationDays,
        IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buyerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryId);
        ArgumentNullException.ThrowIfNull(facets);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(clock);

        if (!AllowedDurationDays.Contains(durationDays))
        {
            throw DomainException.For("bounty.invalid_duration", "A Dicari lasts 7, 14 or 30 days.");
        }

        var now = clock.UtcNow;
        return new Bounty(
            EntityId.New(now),
            buyerId,
            buyerUsername,
            CleanTitle(title),
            categoryId,
            facets,
            CleanDescription(description),
            CleanConditions(conditions),
            RequireBudget(maxBudget),
            CleanCity(shipToCity),
            CleanPhotos(photoKeys),
            now,
            now + TimeSpan.FromDays(durationDays));
    }

    public static Bounty Rehydrate(
        string id,
        string buyerId,
        string? buyerUsername,
        string title,
        string categoryId,
        IReadOnlyDictionary<string, string> facets,
        string description,
        IEnumerable<string> conditions,
        Money maxBudget,
        string shipToCity,
        IEnumerable<string> photoKeys,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        BountyStatus status,
        int proposalCount,
        string? acceptedProposalId,
        string? removedReason,
        DateTimeOffset? dealDueAt,
        long version) =>
        new(id, buyerId, buyerUsername, title, categoryId, facets, description, conditions, maxBudget, shipToCity, photoKeys, createdAt, expiresAt)
        {
            Status = status,
            ProposalCount = proposalCount,
            AcceptedProposalId = acceptedProposalId,
            RemovedReason = removedReason,
            DealDueAt = dealDueAt,
            Version = version,
        };

    /// <summary>Refuses unless <paramref name="userId"/> posted this Dicari.</summary>
    public void EnsureOwnedBy(string? userId)
    {
        if (!string.Equals(BuyerId, userId, StringComparison.Ordinal))
        {
            throw DomainException.For("bounty.forbidden", "Only the buyer who posted this Dicari can do that.");
        }
    }

    /// <summary>
    /// Changes what the buyer is asking for. Only while open and with no deal in flight; the category (and with
    /// it the facets) can change only until the first proposal arrives, because proposals are built on listings
    /// of that category.
    /// </summary>
    public void Edit(
        string actingUserId,
        string title,
        string? description,
        Money maxBudget,
        IEnumerable<string> conditions,
        IEnumerable<string>? photoKeys,
        string? newCategoryId,
        IReadOnlyDictionary<string, string>? newFacets,
        IClock clock)
    {
        EnsureOwnedBy(actingUserId);
        ArgumentNullException.ThrowIfNull(conditions);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureEditable(clock);

        var categoryChanges = newCategoryId is not null && !string.Equals(newCategoryId, CategoryId, StringComparison.Ordinal);
        if (categoryChanges && ProposalCount > 0)
        {
            throw DomainException.For(
                "bounty.category_locked", "The category cannot change once a seller has made a proposal.");
        }

        // Everything is validated before anything is assigned, so a refused edit changes nothing.
        var cleanTitle = CleanTitle(title);
        var cleanDescription = CleanDescription(description);
        var cleanConditions = CleanConditions(conditions);
        var budget = RequireBudget(maxBudget);
        var photos = CleanPhotos(photoKeys);

        Title = cleanTitle;
        Description = cleanDescription;
        MaxBudget = budget;
        _conditions.Clear();
        _conditions.AddRange(cleanConditions);
        _photoKeys.Clear();
        _photoKeys.AddRange(photos);

        if (categoryChanges)
        {
            CategoryId = newCategoryId!;
        }

        if (newFacets is not null)
        {
            _facets.Clear();
            foreach (var (key, value) in newFacets)
            {
                _facets[key] = value;
            }
        }

        Version++;
    }

    /// <summary>The buyer withdraws the Dicari. Refused while a deal is in flight.</summary>
    public void Close(string actingUserId, IClock clock)
    {
        EnsureOwnedBy(actingUserId);
        ArgumentNullException.ThrowIfNull(clock);
        EnsureEditable(clock);

        Status = BountyStatus.Closed;
        Version++;
    }

    /// <summary>
    /// Marks an open Dicari whose deadline has passed as expired. Idempotent; a deal in flight holds expiry off
    /// until it resolves.
    /// </summary>
    /// <returns><c>true</c> when it changed and needs saving.</returns>
    public bool Expire(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Status != BountyStatus.Open || AcceptedProposalId is not null || clock.UtcNow < ExpiresAt)
        {
            return false;
        }

        Status = BountyStatus.Expired;
        Version++;
        return true;
    }

    /// <summary>Staff take it down. Not allowed once it is fulfilled (the deal is done).</summary>
    public void Remove(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (Status is BountyStatus.Fulfilled)
        {
            throw DomainException.For("bounty.already_fulfilled", "A fulfilled Dicari cannot be removed.");
        }

        if (Status == BountyStatus.Removed)
        {
            return;
        }

        Status = BountyStatus.Removed;
        RemovedReason = reason.Trim();
        AcceptedProposalId = null;
        DealDueAt = null;
        Version++;
    }

    /// <summary>A proposal was accepted: the Dicari now waits on that deal and takes nothing new.</summary>
    internal void BeginDeal(string proposalId, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);

        if (!AcceptsProposals(clock))
        {
            throw DomainException.For(
                Status == BountyStatus.Open && AcceptedProposalId is not null ? "bounty.deal_in_progress" : "bounty.not_open",
                "This Dicari is not taking proposals.");
        }

        AcceptedProposalId = proposalId;
        DealDueAt = clock.UtcNow + Offer.CheckoutWindow + DealGrace;
        Version++;
    }

    /// <summary>
    /// The accepted deal fell through (its offer lapsed unpaid): the Dicari opens again, or expires when its own
    /// deadline has passed meanwhile. A no-op when no deal is in flight.
    /// </summary>
    public void ReleaseDeal(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (AcceptedProposalId is null)
        {
            return;
        }

        AcceptedProposalId = null;
        DealDueAt = null;
        if (Status == BountyStatus.Open && clock.UtcNow >= ExpiresAt)
        {
            Status = BountyStatus.Expired;
        }

        Version++;
    }

    /// <summary>An order from the accepted proposal was paid. Idempotent on a Dicari already fulfilled.</summary>
    public void Fulfil()
    {
        if (Status == BountyStatus.Fulfilled)
        {
            return;
        }

        if (Status != BountyStatus.Open || AcceptedProposalId is null)
        {
            throw DomainException.For("bounty.no_deal", "This Dicari has no accepted deal to fulfil.");
        }

        Status = BountyStatus.Fulfilled;
        Version++;
    }

    /// <summary>Mirrors in memory the atomic add the store applies to <see cref="ProposalCount"/>.</summary>
    public void NoteProposalAdded() => ProposalCount++;

    private void EnsureEditable(IClock clock)
    {
        if (Status != BountyStatus.Open || (clock.UtcNow >= ExpiresAt && AcceptedProposalId is null))
        {
            throw DomainException.For("bounty.not_open", "This Dicari is no longer open.");
        }

        if (AcceptedProposalId is not null)
        {
            throw DomainException.For(
                "bounty.deal_in_progress", "A proposal was accepted and is waiting on checkout, so this Dicari cannot change now.");
        }
    }

    private static string CleanTitle(string? title)
    {
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length is < MinTitleLength or > MaxTitleLength)
        {
            throw DomainException.For(
                "bounty.invalid_title", $"The title must be {MinTitleLength} to {MaxTitleLength} characters.");
        }

        RejectContactInfo(trimmed);
        return trimmed;
    }

    private static string CleanDescription(string? description)
    {
        var trimmed = (description ?? string.Empty).Trim();
        if (trimmed.Length > MaxDescriptionLength)
        {
            throw DomainException.For(
                "bounty.description_too_long", $"The description is at most {MaxDescriptionLength} characters.");
        }

        RejectContactInfo(trimmed);
        return trimmed;
    }

    private static Money RequireBudget(Money maxBudget) =>
        maxBudget.IsZero
            ? throw DomainException.For("bounty.invalid_budget", "The budget must be more than zero.")
            : maxBudget;

    private static string CleanCity(string? city)
    {
        var trimmed = (city ?? string.Empty).Trim();
        return trimmed.Length is 0 or > MaxCityLength
            ? throw DomainException.For("bounty.invalid_city", "Choose the city the item should be shipped to.")
            : trimmed;
    }

    private static List<string> CleanConditions(IEnumerable<string> conditions)
    {
        var clean = new List<string>();
        foreach (var raw in conditions)
        {
            var label = (raw ?? string.Empty).Trim();
            if (label.Length is 0 or > MaxConditionLength)
            {
                throw DomainException.For("bounty.invalid_condition", "Pick the conditions you will accept.");
            }

            RejectContactInfo(label);
            if (!clean.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                clean.Add(label);
            }
        }

        return clean.Count is 0 or > MaxConditions
            ? throw DomainException.For("bounty.invalid_condition", "Pick at least one accepted condition.")
            : clean;
    }

    private static List<string> CleanPhotos(IEnumerable<string>? photoKeys)
    {
        var clean = (photoKeys ?? []).Select(key => (key ?? string.Empty).Trim()).ToList();
        if (clean.Any(key => key.Length == 0) || clean.Distinct(StringComparer.Ordinal).Count() != clean.Count)
        {
            throw DomainException.For("bounty.invalid_photos", "A reference photo is missing or repeated.");
        }

        return clean.Count > MaxPhotos
            ? throw DomainException.For("bounty.too_many_photos", $"A Dicari accepts at most {MaxPhotos} reference photos.")
            : clean;
    }

    internal static void RejectContactInfo(string text)
    {
        if (ContactInfo.Contains(text))
        {
            throw DomainException.For(
                "bounty.contact_info",
                "Keep phone numbers, email addresses and links out of the text. Talk to the other person through chat.");
        }
    }
}
