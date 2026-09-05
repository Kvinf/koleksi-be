using Koleksi.Api.Realtime;
using Koleksi.Application.Abstractions;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>The in-process broadcaster that backs the auction event stream.</summary>
public sealed class AuctionEventHubTests
{
    [Fact]
    public async Task A_subscriber_receives_an_update_published_for_its_listing()
    {
        var hub = new AuctionEventHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var subscription = hub.SubscribeAsync("listing-1", cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = subscription.MoveNextAsync().AsTask();

        // SubscribeAsync registers the channel synchronously before its first await, but
        // that only runs once something starts pulling the enumerator — give it a moment.
        await Task.Delay(50, cts.Token);

        var update = SampleUpdate();
        await hub.PublishAsync("listing-1", update, cts.Token);

        (await moveNext).ShouldBeTrue();
        subscription.Current.ShouldBe(update);
    }

    [Fact]
    public async Task A_publish_for_a_different_listing_is_not_delivered()
    {
        var hub = new AuctionEventHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await using var subscription = hub.SubscribeAsync("listing-1", cts.Token).GetAsyncEnumerator(cts.Token);
        var moveNext = subscription.MoveNextAsync().AsTask();

        await Task.Delay(50, CancellationToken.None);
        await hub.PublishAsync("listing-2", SampleUpdate(), CancellationToken.None);

        // Nothing for listing-1 ever arrives, so the subscriber's own timeout is what ends
        // the wait — proof the cross-listing publish was not delivered here.
        await Should.ThrowAsync<OperationCanceledException>(async () => await moveNext);
    }

    [Fact]
    public async Task Publishing_with_no_subscribers_does_not_throw() =>
        await Should.NotThrowAsync(() =>
            new AuctionEventHub().PublishAsync("nobody-listening", SampleUpdate(), CancellationToken.None));

    private static AuctionPriceUpdate SampleUpdate() => new(
        CurrentPriceRupiah: 100_000,
        NextMinimumBidRupiah: 105_000,
        BidCount: 3,
        EndsAt: DateTimeOffset.UtcNow,
        ReserveMet: true,
        MaskedBidder: "ali•••ce",
        PlacedAt: DateTimeOffset.UtcNow);
}
