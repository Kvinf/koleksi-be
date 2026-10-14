using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>Records what the backfill route asked for and answers a fixed page.</summary>
public sealed class FakeOrderSweepBackfill : IOrderSweepBackfill
{
    public string? LastCursor { get; private set; }

    public int LastLimit { get; private set; }

    public int Calls { get; private set; }

    public Task<OrderSweepBackfillPage> BackfillAsync(string? cursor, int limit, CancellationToken ct)
    {
        Calls++;
        LastCursor = cursor;
        LastLimit = limit;
        return Task.FromResult(new OrderSweepBackfillPage(Examined: 200, Updated: 3, NextCursor: "next-page"));
    }
}
