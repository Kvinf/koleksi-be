using Koleksi.Application.Abstractions;
using Koleksi.Domain.Orders;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="ICaseStore"/> holding what the back office would have written for a dispute: case notes of every kind
/// (including the staff-internal one), notices and refunds.
/// </summary>
/// <remarks>
/// Like the real store it never returns an internal note. It deliberately holds one, so a test can prove it is not reachable.
/// </remarks>
public sealed class FakeCaseStore : FakeAdapter, ICaseStore
{
    private readonly object _gate = new();
    private readonly List<DisputeNote> _notes = [];
    private readonly Dictionary<string, (OrderNotice Notice, bool Pending)> _notices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Refund> _refunds = new(StringComparer.Ordinal);

    public void AddNote(DisputeNote note)
    {
        lock (_gate)
        {
            _notes.Add(note);
        }
    }

    public void AddNotice(OrderNotice notice)
    {
        lock (_gate)
        {
            _notices[notice.Id] = (notice, true);
        }
    }

    public void AddRefund(Refund refund)
    {
        lock (_gate)
        {
            _refunds[refund.OrderId] = refund;
        }
    }

    /// <summary>Every note held for the order, including internal ones and replies. For assertions only; nothing in the API reads it.</summary>
    public IReadOnlyList<DisputeNote> NotesOf(string orderId)
    {
        lock (_gate)
        {
            return [.. _notes.Where(note => note.OrderId == orderId)];
        }
    }

    /// <summary>
    /// For each reply written to the order, in order: the time the staff-side marker was asked to be cleared for, or <c>null</c> when
    /// the reply left it alone.
    /// </summary>
    public IReadOnlyList<DateTimeOffset?> ClearedAwaitingBy(string orderId)
    {
        lock (_gate)
        {
            return [.. _replies.Where(reply => reply.OrderId == orderId).Select(reply => reply.Clear)];
        }
    }

    public bool IsPending(string noticeId)
    {
        lock (_gate)
        {
            return _notices[noticeId].Pending;
        }
    }

    public Task<IReadOnlyList<DisputeNote>> ListSharedNotesAsync(string orderId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<DisputeNote> shared = _notes
                .Where(note => note.OrderId == orderId && note.Kind != DisputeNoteKind.Note)
                .OrderBy(note => note.At)
                .ToList();
            return Task.FromResult(shared);
        }
    }

    private readonly List<(string OrderId, DateTimeOffset? Clear)> _replies = [];

    /// <summary>When true the next reply fails as if staff asked again, or the case was ruled on, while it was written.</summary>
    public bool ConflictOnReply { get; set; }

    public Task AddReplyAsync(CaseReplyCommit commit, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            if (ConflictOnReply)
            {
                throw new ConcurrencyConflictException("The case changed while the reply was written.");
            }

            _notes.Add(commit.Reply);
            _replies.Add((commit.Order.Id, commit.ClearAwaitingFor));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrderNotice>> ListPendingNoticesAsync(string userId, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyList<OrderNotice> pending = _notices.Values
                .Where(entry => entry.Pending && entry.Notice.UserId == userId)
                .Select(entry => entry.Notice)
                .OrderByDescending(notice => notice.CreatedAt)
                .ToList();
            return Task.FromResult(pending);
        }
    }

    public Task<IReadOnlyList<OrderNotice>> MarkNoticesDeliveredAsync(
        string userId, IReadOnlyCollection<OrderNotice> notices, DateTimeOffset at, CancellationToken ct)
    {
        ThrowIfFailing();
        var delivered = new List<OrderNotice>();
        lock (_gate)
        {
            // Like the real store: each notice flips only if it is still pending, and only the caller who flipped it is told.
            foreach (var notice in notices.Where(notice => notice.UserId == userId))
            {
                if (_notices.TryGetValue(notice.Id, out var entry) && entry.Notice.UserId == userId && entry.Pending)
                {
                    _notices[notice.Id] = (entry.Notice, false);
                    delivered.Add(notice);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<OrderNotice>>(delivered);
    }

    public Task<IReadOnlyDictionary<string, Refund>> FindRefundsAsync(IReadOnlyCollection<string> orderIds, CancellationToken ct)
    {
        ThrowIfFailing();
        lock (_gate)
        {
            IReadOnlyDictionary<string, Refund> found = orderIds
                .Where(_refunds.ContainsKey)
                .ToDictionary(id => id, id => _refunds[id], StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }
}
