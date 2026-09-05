using Koleksi.Application.Abstractions;

namespace Koleksi.Api.Tests.Fakes;

/// <summary>An in-memory <see cref="ICartRepository"/>.</summary>
public sealed class FakeCartRepository : FakeAdapter, ICartRepository
{
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _carts =
        new(StringComparer.Ordinal);

    public Task AddItemAsync(string buyerId, string listingId, DateTimeOffset addedAt, CancellationToken ct)
    {
        ThrowIfFailing();

        if (!_carts.TryGetValue(buyerId, out var items))
        {
            items = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            _carts[buyerId] = items;
        }

        items[listingId] = addedAt;
        return Task.CompletedTask;
    }

    public Task RemoveItemAsync(string buyerId, string listingId, CancellationToken ct)
    {
        ThrowIfFailing();

        if (_carts.TryGetValue(buyerId, out var items))
        {
            items.Remove(listingId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CartItem>> ListAsync(string buyerId, CancellationToken ct)
    {
        ThrowIfFailing();

        IReadOnlyList<CartItem> items = _carts.TryGetValue(buyerId, out var stored)
            ? stored.Select(pair => new CartItem(pair.Key, pair.Value)).ToList()
            : [];

        return Task.FromResult(items);
    }
}
