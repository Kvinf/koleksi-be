using Koleksi.Application.Abstractions;
using Koleksi.Application.Common;
using Koleksi.Application.Features.Standing;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IPayoutRepository"/> with the same one-per-order and version rules as storage.</summary>
public sealed class FakePayoutRepository : FakeAdapter, IPayoutRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Payout> _payouts = new(StringComparer.Ordinal);

    public IReadOnlyCollection<Payout> All
    {
        get
        {
            lock (_gate)
            {
                return _payouts.Values.Select(Copy).ToList();
            }
        }
    }

    public Task<bool> AddIfAbsentAsync(Payout payout, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (_payouts.ContainsKey(payout.OrderId))
            {
                return Task.FromResult(false);
            }

            _payouts[payout.OrderId] = Copy(payout);
            return Task.FromResult(true);
        }
    }

    public Task<Payout?> FindByOrderAsync(string orderId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_payouts.TryGetValue(orderId, out var found) ? Copy(found) : null);
        }
    }

    public Task UpdateAsync(Payout payout, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (!_payouts.TryGetValue(payout.OrderId, out var stored) || stored.Version != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Payout for order '{payout.OrderId}' changed since it was read.");
            }

            _payouts[payout.OrderId] = Copy(payout);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<Payout>> ListDueAsync(DateTimeOffset asOf, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<Payout> due = _payouts.Values
                .Where(p => p.Status == PayoutStatus.Pending && p.NextAttemptAt <= asOf)
                .OrderBy(p => p.NextAttemptAt).Take(limit).Select(Copy).ToList();
            return Task.FromResult(due);
        }
    }

    public Task<Page<Payout>> ListForSellerAsync(string sellerId, string? cursor, int limit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var items = _payouts.Values.Where(p => p.SellerId == sellerId)
                .OrderByDescending(p => p.CreatedAt).Take(limit).Select(Copy).ToList();
            return Task.FromResult(new Page<Payout>(items, null, items.Count));
        }
    }

    /// <summary>The transaction half of release: the order is saved by the order fake, the payout here.</summary>
    internal void AddForRelease(Payout payout)
    {
        lock (_gate)
        {
            if (!_payouts.TryAdd(payout.OrderId, Copy(payout)))
            {
                throw new ConcurrencyConflictException($"Payout for order '{payout.OrderId}' already exists.");
            }
        }
    }

    private static Payout Copy(Payout p) => Payout.Rehydrate(
        p.OrderId, p.OrderNumber, p.SellerId, p.Amount, p.Fee, p.Status, p.CreatedAt, p.NextAttemptAt, p.PaidAt,
        p.ProviderReference, p.Attempts, p.LastError, p.Version);
}

/// <summary>An in-memory <see cref="IAccountStandingRepository"/> with the same version rules as storage.</summary>
public sealed class FakeAccountStandingRepository : FakeAdapter, IAccountStandingRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountStanding> _standings = new(StringComparer.Ordinal);

    public Task<AccountStanding?> FindAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            return Task.FromResult(_standings.TryGetValue(userId, out var found) ? Copy(found) : null);
        }
    }

    public Task SaveAsync(AccountStanding standing, long expectedVersion, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            var exists = _standings.TryGetValue(standing.UserId, out var stored);
            if (expectedVersion == 0 ? exists : !exists || stored!.Version != expectedVersion)
            {
                throw new ConcurrencyConflictException($"Standing of '{standing.UserId}' changed since it was read.");
            }

            _standings[standing.UserId] = Copy(standing);
            return Task.CompletedTask;
        }
    }

    /// <summary>Puts an account into a given state, for tests.</summary>
    public void Seed(AccountStanding standing)
    {
        lock (_gate)
        {
            _standings[standing.UserId] = Copy(standing);
        }
    }

    private static AccountStanding Copy(AccountStanding s) =>
        AccountStanding.Rehydrate(s.UserId, s.StoredWarnings, s.LastWarningAt, s.BannedAt, s.Recent, s.Version);
}

/// <summary>A courier whose answers a test sets.</summary>
public sealed class FakeDeliveryTracker : IDeliveryTracker
{
    public bool IsConfigured { get; set; } = true;

    private bool? canPoll;

    /// <summary>Whether the sweep should ask. Follows <see cref="IsConfigured"/> unless a test sets it: a push-only provider is configured but never asked.</summary>
    public bool CanPoll
    {
        get => canPoll ?? IsConfigured;
        set => canPoll = value;
    }

    /// <summary>Orders the provider was told about when they shipped.</summary>
    public List<string> Registered { get; } = [];

    /// <summary>When set, telling the provider fails, as a down provider would.</summary>
    public bool FailRegistration { get; set; }

    public Task RegisterShipmentAsync(Order order, CancellationToken ct)
    {
        if (FailRegistration)
        {
            throw new HttpRequestException("provider down");
        }

        Registered.Add(order.OrderNumber);
        return Task.CompletedTask;
    }

    public DeliveryReport Report { get; set; } = DeliveryReport.Unknown;

    public List<string> Asked { get; } = [];

    public Task<DeliveryReport> CheckAsync(Order order, CancellationToken ct)
    {
        Asked.Add(order.Id);
        return Task.FromResult(Report);
    }
}

/// <summary>A payout provider that records what it was asked to send and answers as a test says.</summary>
public sealed class FakeEscrowPayout : IEscrowPayout
{
    public PayoutResult NextResult { get; set; } = PayoutResult.Sent("fake-ref");

    public List<(string OrderId, long AmountRupiah, BankAccount? Destination)> Sent { get; } = [];

    public Task<PayoutResult> SendAsync(Payout payout, BankAccount? destination, CancellationToken ct)
    {
        Sent.Add((payout.OrderId, payout.Amount.Rupiah, destination));
        return Task.FromResult(NextResult);
    }
}

/// <summary>
/// A courier-tracking provider's webhook for tests: the body is "eventId|orderNumber|status" and the
/// signature header must equal "ok". Stands in for a real adapter, whose own signature scheme is tested in
/// the infrastructure tests.
/// </summary>
public sealed class FakeDeliveryWebhookProvider : IDeliveryWebhookProvider
{
    public string Name => "fake";

    /// <summary>When set, the key cannot be read, as when Secrets Manager is unavailable.</summary>
    public bool KeyUnavailable { get; set; }

    public Task<DeliveryNotification?> VerifyAndParseAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (KeyUnavailable)
        {
            throw new DeliveryVerificationUnavailableException("no key");
        }

        if (!headers.TryGetValue("X-Test-Signature", out var signature) || signature != "ok")
        {
            return Task.FromResult<DeliveryNotification?>(null);
        }

        var parts = rawBody.Split('|');
        if (parts.Length != 3)
        {
            return Task.FromResult<DeliveryNotification?>(null);
        }

        var state = parts[2] switch
        {
            "delivered" => DeliveryState.Delivered,
            "in_transit" => DeliveryState.InTransit,
            _ => DeliveryState.Unknown,
        };

        return Task.FromResult<DeliveryNotification?>(new DeliveryNotification(parts[0], parts[1], state));
    }
}
