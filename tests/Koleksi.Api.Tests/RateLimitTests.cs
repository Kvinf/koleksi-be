using System.Net;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// The bidding bucket.
/// </summary>
/// <remarks>
/// <para>
/// Bidding gets a tighter budget than everything else because it is the most abusable
/// write in the product and the most expensive to serve under contention.
/// </para>
/// <para>
/// The partition is what makes it usable at all. Keyed on the caller's subject, one
/// account hammering a lot cannot spend anyone else's budget — whereas an IP-only key
/// would let a single user behind a shared mobile NAT exhaust the allowance for a whole
/// city.
/// </para>
/// </remarks>
public sealed class RateLimitTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    /// <summary>The bidding bucket holds fifteen tokens; twenty attempts must overrun it.</summary>
    private const int Attempts = 20;

    [Fact]
    public async Task One_account_hammering_a_lot_is_cut_off()
    {
        var listingId = Seed();
        using var client = factory.CreateClientAs(
            "a1b2c3d4-0000-4000-8000-000000000001", "penawar.cepat");

        var statuses = await FireAsync(client, listingId);

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_throttled_caller_is_told_when_to_come_back()
    {
        var listingId = Seed();
        using var client = factory.CreateClientAs(
            "a1b2c3d4-0000-4000-8000-000000000002", "penawar.dua");

        HttpResponseMessage? throttled = null;
        try
        {
            for (var attempt = 0; attempt < Attempts && throttled is null; attempt++)
            {
                var response = await client.PostBidAsync(listingId, 1_500_000);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throttled = response;
                }
                else
                {
                    response.Dispose();
                }
            }

            throttled.ShouldNotBeNull();
            throttled.Headers.RetryAfter.ShouldNotBeNull();

            var body = await throttled.ReadJsonAsync();
            body.GetProperty("code").GetString().ShouldBe("rate_limited");
        }
        finally
        {
            throttled?.Dispose();
        }
    }

    [Fact]
    public async Task Exhausting_one_accounts_budget_leaves_another_account_untouched()
    {
        // The whole point of partitioning by subject. If this fails, one determined bidder
        // can lock every other bidder out of the last minute of an auction.
        var listingId = Seed();

        using var noisy = factory.CreateClientAs(
            "a1b2c3d4-0000-4000-8000-000000000003", "penawar.berisik");

        var noisyStatuses = await FireAsync(noisy, listingId);
        noisyStatuses.ShouldContain(HttpStatusCode.TooManyRequests);

        using var quiet = factory.CreateClientAs(
            "a1b2c3d4-0000-4000-8000-000000000004", "penawar.tenang");

        using var response = await quiet.PostBidAsync(listingId, 1_500_000);

        response.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    /// Fires the whole budget at one auction and returns every status observed.
    /// </summary>
    /// <remarks>
    /// The bids themselves are expected to be refused — after the first, each one repeats
    /// a maximum the caller already holds. That is deliberate: the limiter runs before the
    /// endpoint, so the test measures throttling without also depending on the outcome of
    /// twenty auction rules.
    /// </remarks>
    private static async Task<List<HttpStatusCode>> FireAsync(HttpClient client, string listingId)
    {
        var statuses = new List<HttpStatusCode>(Attempts);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            using var response = await client.PostBidAsync(listingId, 1_500_000);
            statuses.Add(response.StatusCode);
        }

        return statuses;
    }

    private string Seed()
    {
        var listing = TestData.ActiveListing(
            price: Money.FromRupiah(1_000_000), format: ListingFormat.Auction);

        factory.Listings.Seed(listing);
        factory.Auctions.Seed(TestData.LiveAuction(listing, Money.FromRupiah(1_000_000)));

        return listing.Id;
    }
}
