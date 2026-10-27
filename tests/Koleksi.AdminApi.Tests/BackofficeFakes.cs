using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.AdminApi.Tests;

// In-memory stand-ins for the adapters the back office's Milestone 1 reads and writes through. They keep exactly the behaviour the
// HTTP tests depend on and nothing else; the real adapters are proved against DynamoDB Local in Koleksi.Infrastructure.IntegrationTests.

public sealed class FakeOrderRepository : IOrderRepository
{
    private readonly Dictionary<string, Order> byId = new(StringComparer.Ordinal);

    public void Add(Order order)
    {
        lock (byId)
        {
            byId[order.Id] = order;
        }
    }

    /// <summary>Independent copies of every stored order, as a reader of the real table would get.</summary>
    public IReadOnlyList<Order> All
    {
        get
        {
            lock (byId)
            {
                return [.. byId.Values.Select(Clone)];
            }
        }
    }

    /// <summary>The stored order as it is right now, or <c>null</c>. A copy: changing it changes nothing until it is committed.</summary>
    public Order? Current(string orderId)
    {
        lock (byId)
        {
            return byId.TryGetValue(orderId, out var order) ? Clone(order) : null;
        }
    }

    /// <summary>Replaces the stored order with the committed one, as the real store's transaction does.</summary>
    public void Commit(Order order)
    {
        lock (byId)
        {
            byId[order.Id] = Clone(order);
        }
    }

    /// <summary>Whether the stored order is still disputed and still at <paramref name="version"/>: the real store's write condition.</summary>
    public bool IsDisputedAt(string orderId, long version)
    {
        lock (byId)
        {
            return byId.TryGetValue(orderId, out var order) && order.EscrowState == EscrowState.Disputed && order.Version == version;
        }
    }

    public Task<Order?> FindAsync(string orderId, CancellationToken ct) => Task.FromResult(Current(orderId));

    public Task<Order?> FindByOrderNumberAsync(string orderNumber, CancellationToken ct) =>
        Task.FromResult(All.FirstOrDefault(o => o.OrderNumber == orderNumber));

    private static Order Clone(Order o) =>
        Order.Rehydrate(
            o.Id, o.OrderNumber, o.BuyerId, o.SellerId, o.Lines, o.ShipTo, o.Courier, o.EscrowState, o.Shipment, o.Dispute,
            o.CreatedAt, o.PaidAt, o.ShippedAt, o.DeliveredAt, o.ReleasedAt, o.Version, o.FeePercentSnapshot, o.Source,
            o.PaymentDueAt, o.TrackingCheckedAt, o.SourceReference);

    public Task AddManyAsync(IReadOnlyList<Order> orders, CancellationToken ct)
    {
        foreach (var order in orders)
        {
            Add(order);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(Order order, long expectedVersion, CancellationToken ct) =>
        throw new NotSupportedException("The admin API never updates an order outside a dispute commit.");

    public Task UpdateWithPayoutAsync(Order order, long expectedVersion, Payout payout, CancellationToken ct) =>
        throw new NotSupportedException("The admin API writes a payout only inside a dispute commit.");

    public Task<IReadOnlyList<Order>> ListDisputedForUserAsync(string userId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>(
            [.. All.Where(o => o.Dispute is not null && (o.BuyerId == userId || o.SellerId == userId)).OrderByDescending(o => o.CreatedAt)]);

    public Task<Page<Order>> ListForBuyerAsync(string buyerId, string? cursor, int limit, CancellationToken ct) =>
        Task.FromResult(Paged(All.Where(o => o.BuyerId == buyerId), cursor, limit));

    public Task<Page<Order>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct) =>
        Task.FromResult(Paged(All.Where(o => o.SellerId == sellerId), cursor, limit));

    public Task<IReadOnlyList<Order>> ListDueForAutoReleaseAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>([]);

    public Task<IReadOnlyList<Order>> ListPaymentExpiredAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>([]);

    public Task<IReadOnlyList<Order>> ListDueForTrackingAsync(DateTimeOffset asOf, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>([]);

    /// <summary>Newest first; the cursor is just how many have been returned already.</summary>
    private static Page<Order> Paged(IEnumerable<Order> source, string? cursor, int limit)
    {
        var ordered = source.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        var skip = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var items = ordered.Skip(skip).Take(limit).ToList();
        var next = skip + items.Count < ordered.Count ? (skip + items.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new Page<Order>(items, next, ordered.Count);
    }
}

/// <summary>
/// The dispute store, committing each change together with its audit entry the way the real one does in a transaction: the audit
/// entry is written first, and if that fails nothing else is stored.
/// </summary>
public sealed class FakeDisputeStore(FakeOrderRepository orders, FakeAuditLog audit) : IDisputeAdminStore
{
    private readonly object gate = new();
    private readonly List<DisputeNote> notes = [];
    private readonly HashSet<string> awaitingInfo = new(StringComparer.Ordinal);
    private readonly HashSet<string> resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Refund> refunds = new(StringComparer.Ordinal);
    private readonly List<Payout> payouts = [];
    private readonly List<OrderNotice> notices = [];

    /// <summary>Make the next ruling lose a race: another ruling (for the seller) lands first, and the store reports the order changed.</summary>
    public bool ConflictOnNextResolve { get; set; }

    /// <summary>Make the next ruling fail with a conflict that has no winner behind it: the order is still disputed afterwards.</summary>
    public bool SpuriousConflictOnNextResolve { get; set; }

    public IReadOnlyList<Payout> Payouts
    {
        get
        {
            lock (gate)
            {
                return [.. payouts];
            }
        }
    }

    public IReadOnlyList<OrderNotice> Notices
    {
        get
        {
            lock (gate)
            {
                return [.. notices];
            }
        }
    }

    public IReadOnlyList<DisputeNote> Notes
    {
        get
        {
            lock (gate)
            {
                return [.. notes];
            }
        }
    }

    /// <summary>Puts a note in the case file the way the public site's store would for a reply, which staff do not write.</summary>
    public void SeedNote(DisputeNote note)
    {
        lock (gate)
        {
            notes.Add(note);
        }
    }

    public Refund? RefundFor(string orderId)
    {
        lock (gate)
        {
            // A copy, as a read from a table is: changing it does not change what is stored until a commit says so.
            return refunds.GetValueOrDefault(orderId) is { } stored
                ? Refund.Rehydrate(
                    stored.Id, stored.OrderId, stored.OrderNumber, stored.BuyerId, stored.Amount, stored.Reason, stored.StaffId,
                    stored.StaffName, stored.CreatedAt, stored.Status, stored.StatusAt,
                    stored.Remainder, stored.Disposition, stored.SellerPayout, stored.SettledAt)
                : null;
        }
    }

    public Task<IReadOnlyList<Order>> ListOpenAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>(
            [.. orders.All.Where(o => o.EscrowState == EscrowState.Disputed).OrderBy(o => o.Dispute!.OpenedAt).Take(limit)]);

    public Task<IReadOnlyList<Order>> ListResolvedAsync(int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>(
            [.. orders.All.Where(o => o.Dispute?.Resolution is not null).OrderByDescending(o => o.Dispute!.ResolvedAt).Take(limit)]);

    public Task<IReadOnlyList<DisputeNote>> ListNotesAsync(string orderId, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<DisputeNote>>([.. notes.Where(n => n.OrderId == orderId).OrderBy(n => n.At)]);
        }
    }

    public Task<IReadOnlySet<string>> AwaitingInfoAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlySet<string>>(orderIds.Where(awaitingInfo.Contains).ToHashSet(StringComparer.Ordinal));
        }
    }

    public Task<Refund?> FindRefundAsync(string orderId, CancellationToken ct) => Task.FromResult(RefundFor(orderId));

    public async Task AddNoteAsync(DisputeNoteCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        if (!orders.IsDisputedAt(commit.Order.Id, commit.Order.Version))
        {
            throw new ConcurrencyConflictException("The order is no longer disputed.");
        }

        await audit.AppendAsync(commit.Audit, ct).ConfigureAwait(false);

        lock (gate)
        {
            notes.Add(commit.Note);
            notices.AddRange(commit.Notices);
            if (commit.MarkAwaitingInfo)
            {
                awaitingInfo.Add(commit.Order.Id);
            }
        }
    }

    public async Task ResolveAsync(DisputeResolutionCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (gate)
        {
            if (ConflictOnNextResolve)
            {
                // Another member of staff gets in first: their ruling lands (for the seller) between this one's read and its write.
                ConflictOnNextResolve = false;
                var winner = orders.Current(commit.Order.Id)!;
                winner.ResolveDispute(DisputeResolution.ReleaseToSeller, new FixedClock(commit.Order.Dispute!.OpenedAt));
                orders.Commit(winner);
            }

            if (SpuriousConflictOnNextResolve)
            {
                SpuriousConflictOnNextResolve = false;
                throw new ConcurrencyConflictException("The table could not take the write.");
            }

            if (!orders.IsDisputedAt(commit.Order.Id, commit.ExpectedVersion))
            {
                throw new ConcurrencyConflictException("The order changed since it was read.");
            }
        }

        await audit.AppendAsync(commit.Audit, ct).ConfigureAwait(false);

        lock (gate)
        {
            orders.Commit(commit.Order);
            resolved.Add(commit.Order.Id);
            awaitingInfo.Remove(commit.Order.Id);
            notes.Add(commit.Decision);
            notices.AddRange(commit.Notices);
            if (commit.Payout is { } payout)
            {
                payouts.Add(payout);
            }

            if (commit.Refund is { } refund)
            {
                refunds[commit.Order.Id] = refund;
            }
        }
    }

    public async Task SettleRemainderAsync(RemainderSettlementCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);

        lock (gate)
        {
            // Like the real store: the stored refund must still be held, whatever the caller's copy says.
            if (!refunds.TryGetValue(commit.Order.Id, out var stored) || stored.Disposition != RemainderDisposition.Held)
            {
                throw new ConcurrencyConflictException("The remainder is no longer held.");
            }
        }

        await audit.AppendAsync(commit.Audit, ct).ConfigureAwait(false);

        lock (gate)
        {
            refunds[commit.Order.Id] = commit.Refund;
            payouts.Add(commit.Payout);
        }
    }

    public Task SaveRefundOutcomeAsync(Refund refund, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refund);

        lock (gate)
        {
            refunds[refund.OrderId] = refund;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeHolidayStore(FakeAuditLog log) : IHolidayStore
{
    private readonly object gate = new();
    private readonly SortedDictionary<string, Holiday> byDate = new(StringComparer.Ordinal);
    private readonly HashSet<int> seededYears = [];

    public int Count
    {
        get
        {
            lock (gate)
            {
                return byDate.Count;
            }
        }
    }

    /// <summary>Starts the store already holding a year, as it would after the first start, so a host that boots does not seed (and audit) again.</summary>
    public void Preload(int year, IEnumerable<Holiday> seed)
    {
        lock (gate)
        {
            seededYears.Add(year);
            foreach (var holiday in seed)
            {
                byDate[holiday.Date] = holiday;
            }
        }
    }

    public Task<IReadOnlyList<Holiday>> ListAsync(int year, CancellationToken ct)
    {
        lock (gate)
        {
            var prefix = year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-";
            return Task.FromResult<IReadOnlyList<Holiday>>([.. byDate.Values.Where(h => h.Date.StartsWith(prefix, StringComparison.Ordinal))]);
        }
    }

    public Task<Holiday?> FindAsync(string isoDate, CancellationToken ct)
    {
        lock (gate)
        {
            return Task.FromResult(byDate.GetValueOrDefault(isoDate));
        }
    }

    public async Task<HolidayWrite> AddAsync(Holiday holiday, AuditEntry audit, CancellationToken ct)
    {
        lock (gate)
        {
            if (byDate.ContainsKey(holiday.Date))
            {
                return HolidayWrite.AlreadyExists;
            }
        }

        await log.AppendAsync(audit, ct).ConfigureAwait(false);
        lock (gate)
        {
            byDate[holiday.Date] = holiday;
        }

        return HolidayWrite.Written;
    }

    public async Task<HolidayRemoval> RemoveAsync(string isoDate, AuditEntry audit, CancellationToken ct)
    {
        lock (gate)
        {
            if (!byDate.ContainsKey(isoDate))
            {
                return HolidayRemoval.NotFound;
            }
        }

        await log.AppendAsync(audit, ct).ConfigureAwait(false);
        lock (gate)
        {
            byDate.Remove(isoDate);
        }

        return HolidayRemoval.Removed;
    }

    public async Task<bool> SeedAsync(int year, IReadOnlyList<Holiday> holidays, AuditEntry audit, CancellationToken ct)
    {
        lock (gate)
        {
            if (!seededYears.Add(year))
            {
                return false;
            }
        }

        await log.AppendAsync(audit, ct).ConfigureAwait(false);
        lock (gate)
        {
            foreach (var holiday in holidays)
            {
                byDate[holiday.Date] = holiday;
            }
        }

        return true;
    }
}

public sealed class FakePublicProfiles : IPublicProfileRepository
{
    private readonly Dictionary<string, PublicProfile> byUser = new(StringComparer.Ordinal);

    public void Add(PublicProfile profile) => byUser[profile.UserId] = profile;

    public Task<PublicProfile?> FindByUserAsync(string userId, CancellationToken ct) => Task.FromResult(byUser.GetValueOrDefault(userId));

    public Task<IReadOnlyDictionary<string, PublicProfile>> FindManyByUserAsync(IReadOnlyCollection<string> userIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, PublicProfile>>(
            userIds.Where(byUser.ContainsKey).Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => byUser[id], StringComparer.Ordinal));

    public Task<PublicProfile?> FindByHandleAsync(string handle, CancellationToken ct) =>
        Task.FromResult(byUser.Values.FirstOrDefault(p => p.Handle == handle));

    public Task<PublicProfileWriteResult> CreateAsync(PublicProfile profile, CancellationToken ct) =>
        throw new NotSupportedException("The admin API never creates a public profile.");

    public Task<PublicProfileWriteResult> SaveAsync(PublicProfile profile, string previousHandle, long expectedVersion, CancellationToken ct) =>
        throw new NotSupportedException("The admin API never edits a public profile.");
}

public sealed class FakeConversations : IConversationRepository
{
    private readonly Dictionary<string, Conversation> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChatMessage>> messages = new(StringComparer.Ordinal);

    public Task<Conversation?> FindAsync(string conversationId, CancellationToken ct) =>
        Task.FromResult(conversations.GetValueOrDefault(conversationId));

    public Task AppendAsync(Conversation conversation, ChatMessage message, CancellationToken ct)
    {
        conversations[conversation.Id] = conversation;
        if (!messages.TryGetValue(conversation.Id, out var thread))
        {
            messages[conversation.Id] = thread = [];
        }

        thread.Add(message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(string conversationId, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(
            messages.TryGetValue(conversationId, out var thread) ? [.. thread.OrderBy(m => m.SentAt).TakeLast(limit)] : []);

    public Task MarkReadAsync(string conversationId, ConversationRole role, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<Conversation>> ListForUserAsync(string userId, int limit, CancellationToken ct) =>
        throw new NotSupportedException("The admin API reads one order's chat, never a person's inbox.");
}

public sealed class FakeRefundSender : IRefundSender
{
    private int calls;

    public RefundStatus Outcome { get; set; } = RefundStatus.PendingGateway;

    public bool Fail { get; set; }

    public int Calls => Volatile.Read(ref calls);

    public Task<RefundStatus> SendAsync(Refund refund, CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        return Fail ? throw new InvalidOperationException("The gateway is unreachable.") : Task.FromResult(Outcome);
    }
}

internal sealed class FixedClock(DateTimeOffset now) : Koleksi.Domain.Common.IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}