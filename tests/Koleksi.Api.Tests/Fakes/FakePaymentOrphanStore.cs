using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="IPaymentOrphanStore"/> that keeps what was recorded, once per payment.</summary>
public sealed class FakePaymentOrphanStore : FakeAdapter, IPaymentOrphanStore
{
    private readonly List<PaymentOrphan> _recorded = [];
    private readonly object _gate = new();

    public IReadOnlyList<PaymentOrphan> Recorded
    {
        get
        {
            lock (_gate)
            {
                return _recorded.ToList();
            }
        }
    }

    public Task RecordAsync(PaymentOrphan orphan, CancellationToken ct)
    {
        ThrowIfFailing();
        ArgumentNullException.ThrowIfNull(orphan);

        lock (_gate)
        {
            var key = string.IsNullOrWhiteSpace(orphan.ProcessorReference) ? orphan.EventId : orphan.ProcessorReference;
            if (!_recorded.Exists(existing => existing.OrderNumber == orphan.OrderNumber
                && (string.IsNullOrWhiteSpace(existing.ProcessorReference) ? existing.EventId : existing.ProcessorReference) == key))
            {
                _recorded.Add(orphan);
            }
        }

        return Task.CompletedTask;
    }
}
