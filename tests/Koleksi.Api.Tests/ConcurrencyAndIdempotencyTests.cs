using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Two requests racing for the same thing: identical offers, and one Idempotency-Key used more than once.
/// </summary>
public sealed class ConcurrencyAndIdempotencyTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    // ---- N-02: parallel identical offers ------------------------------------------------------

    [Fact]
    public async Task Five_parallel_identical_offers_give_one_success_and_four_already_active()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        // Hold every request at the "is there an open offer?" check until all five have read "no", so
        // all five really reach the write and four of them really lose it there.
        factory.Offers.HoldFindActiveUntilCallers(5);
        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                client.PostAsJsonAsync($"/api/listings/{listing.Id}/offers", new { amountRupiah = 2_000_000 })));

            try
            {
                responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);

                var losers = responses.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
                losers.Count.ShouldBe(4);
                foreach (var loser in losers)
                {
                    loser.StatusCode.ShouldBe(HttpStatusCode.Conflict);
                    (await loser.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("offer.already_active");
                }
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            factory.Offers.HoldFindActiveUntilCallers(0);
        }

        // One offer stored, not five.
        var stored = await factory.Offers.ListForBuyerAsync(TestData.BidderId, null, 50, CancellationToken.None);
        stored.Items.Count(o => o.ListingId == listing.Id).ShouldBe(1);
    }

    // ---- N-03 / D-06: a key that is claimed but has no result yet ----------------------------------

    [Fact]
    public async Task A_checkout_whose_key_is_still_in_flight_is_refused_not_run_again()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        var key = Guid.NewGuid().ToString();

        // The first request has claimed the key and is still working: claimed, no result recorded.
        (await factory.Idempotency.TryClaimAsync($"order.checkout:{TestData.BidderId}", key, Retention, default))
            .ShouldBeTrue();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await PostCheckoutAsync(client, listing.Id, key);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("request.in_progress");
        response.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Three_parallel_checkouts_with_one_key_never_make_three_orders()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        var key = Guid.NewGuid().ToString();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => PostCheckoutAsync(client, listing.Id, key)));

        try
        {
            // Each is either the one real answer (or a replay of it) or a refusal to run twice.
            responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
            responses.ShouldContain(r => r.StatusCode == HttpStatusCode.OK);

            var bodies = new List<string>();
            foreach (var ok in responses.Where(r => r.StatusCode == HttpStatusCode.OK))
            {
                bodies.Add(await ok.Content.ReadAsStringAsync());
            }

            bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(1, "every success must point at the same order");

            foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                (await refused.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("request.in_progress");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // ---- Keys are per user -------------------------------------------------------------------

    [Fact]
    public async Task One_key_used_by_two_users_does_not_share_a_response()
    {
        var key = Guid.NewGuid().ToString();
        using var first = factory.CreateClientAs("01JIDEMUSERAAAAAAAAAAAAAAA", "idem.first");
        using var second = factory.CreateClientAs("01JIDEMUSERBBBBBBBBBBBBBBB", "idem.second");

        var firstId = await CreateDraftAsync(first, key);
        var secondId = await CreateDraftAsync(second, key);

        secondId.ShouldNotBe(firstId, "the second user must get their own listing, not the first user's recorded answer");
        var stored = await factory.Listings.FindAsync(secondId, CancellationToken.None);
        stored.ShouldNotBeNull();
        stored.SellerId.ShouldBe("01JIDEMUSERBBBBBBBBBBBBBBB");
    }

    // ---- The policy itself -------------------------------------------------------------------

    [Fact]
    public async Task A_second_call_while_the_first_is_running_is_rejected_when_the_policy_says_so()
    {
        var store = new FakeIdempotencyStore(new MutableClock());
        var release = new TaskCompletionSource();
        var calls = 0;

        var first = store.RunOnceAsync(
            "scope", "key", Retention, async () => { Interlocked.Increment(ref calls); await release.Task; return 1; },
            default, InFlightPolicy.Reject);

        await Should.ThrowAsync<RequestInProgressException>(() => store.RunOnceAsync(
            "scope", "key", Retention, () => { Interlocked.Increment(ref calls); return Task.FromResult(2); },
            default, InFlightPolicy.Reject));

        release.SetResult();
        (await first).ShouldBe(1);
        calls.ShouldBe(1);

        // And once the first has finished, the duplicate replays its answer rather than being refused.
        (await store.RunOnceAsync("scope", "key", Retention, () => Task.FromResult(3), default, InFlightPolicy.Reject))
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_default_policy_still_runs_a_second_call_again()
    {
        // Cheap, harmless endpoints keep their behaviour: a double-tap is not worth a "try again".
        var store = new FakeIdempotencyStore(new MutableClock());
        (await store.TryClaimAsync("scope", "key", Retention, default)).ShouldBeTrue();

        (await store.RunOnceAsync("scope", "key", Retention, () => Task.FromResult(7), default)).ShouldBe(7);
    }

    [Fact]
    public async Task A_request_that_fails_gives_its_key_back_so_the_retry_runs()
    {
        var store = new FakeIdempotencyStore(new MutableClock());

        await Should.ThrowAsync<InvalidOperationException>(() => store.RunOnceAsync<int>(
            "scope", "key", Retention, () => throw new InvalidOperationException("boom"),
            default, InFlightPolicy.Reject));

        (await store.RunOnceAsync(
            "scope", "key", Retention, () => Task.FromResult(5), default, InFlightPolicy.Reject)).ShouldBe(5);
    }

    [Fact]
    public async Task A_claim_whose_request_died_does_not_lock_the_key_forever()
    {
        var clock = new MutableClock();
        var store = new FakeIdempotencyStore(clock);

        // Claimed, then the Lambda was killed: no result, no release.
        (await store.TryClaimAsync("scope", "key", Retention, default)).ShouldBeTrue();
        await Should.ThrowAsync<RequestInProgressException>(() => store.RunOnceAsync(
            "scope", "key", Retention, () => Task.FromResult(1), default, InFlightPolicy.Reject));

        clock.Advance(IdempotencyExtensions.InFlightLease + TimeSpan.FromSeconds(1));

        (await store.RunOnceAsync(
            "scope", "key", Retention, () => Task.FromResult(2), default, InFlightPolicy.Reject)).ShouldBe(2);
    }

    [Fact]
    public async Task A_recorded_result_is_never_released()
    {
        var store = new FakeIdempotencyStore(new MutableClock());
        (await store.RunOnceAsync("scope", "key", Retention, () => Task.FromResult(1), default)).ShouldBe(1);

        await store.ReleaseClaimAsync("scope", "key", default);

        (await store.RunOnceAsync("scope", "key", Retention, () => Task.FromResult(2), default)).ShouldBe(1);
    }

    private static Task<HttpResponseMessage> PostCheckoutAsync(HttpClient client, string listingId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders/checkout")
        {
            Content = JsonContent.Create(new
            {
                source = "FixedPrice",
                referenceId = listingId,
                shipTo = new
                {
                    recipientName = "Rangga Wijaya",
                    phone = "081234567890",
                    street = "Jl. Merdeka No. 1",
                    city = "Bandung",
                    province = "Jawa Barat",
                    postalCode = "40111",
                },
                courierCode = "jne",
            }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<string> CreateDraftAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/listings")
        {
            Content = JsonContent.Create(new
            {
                categoryId = TestData.CardsCategoryId,
                title = "PSA 10 Charizard Base Set Holo 1999",
                priceRupiah = 2_500_000,
                format = "FixedPrice",
                condition = "PSA 10",
                city = "Bandung",
                description = "Kondisi mulus, kartu asli.",
                photos = Array.Empty<object>(),
                publish = false,
            }),
        };
        request.Headers.Add("Idempotency-Key", key);

        using var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.ReadJsonAsync()).GetProperty("listingId").GetString()!;
    }
}
