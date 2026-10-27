using System.Net;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// Public catalogue reads have a budget of their own.
/// </summary>
/// <remarks>
/// Found by QA: every page view costs the web tier several catalogue calls, so on the general
/// per-address budget (120, then 60 a minute) about fifteen page views a minute from one address made
/// every page answer with an error. A mobile carrier's shared address makes that ordinary traffic.
/// The catalogue now has a larger bucket of its own, and sharing nothing with the general one is what
/// keeps spending it from locking anyone out of anything else.
/// </remarks>
public sealed class CatalogBurstTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task A_burst_of_page_views_from_one_address_is_not_throttled()
    {
        using var client = factory.CreateClient();

        // 300 catalogue reads is far past the general budget of 120 and well inside the catalogue's own.
        var throttled = 0;
        for (var i = 0; i < 300; i++)
        {
            using var response = await client.GetAsync("/api/catalog/categories");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled++;
            }
        }

        throttled.ShouldBe(0);
    }
}

/// <summary>Spending the general budget leaves the catalogue answering.</summary>
public sealed class GeneralBudgetIsSeparateTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task Spending_the_general_budget_does_not_take_the_catalogue_down()
    {
        // The limiter runs after authorization, so only a signed-in caller can spend the general budget
        // on a protected route; an anonymous one is turned away before it.
        using var client = factory.CreateClientAs("a1b2c3d4-0000-4000-8000-0000000000c1", "pembaca.rajin");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 150; i++)
        {
            // A real route outside the catalogue, so this spends the general budget (per account).
            using var response = await client.GetAsync("/api/orders/mine");
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);

        using var catalogue = await client.GetAsync("/api/catalog/categories");
        catalogue.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

/// <summary>A seller's public shop is a public read like the catalogue.</summary>
public sealed class ShopReadsShareCatalogueBudgetTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task A_sellers_public_shop_shares_the_catalogue_budget()
    {
        using var client = factory.CreateClient();

        // The shop page is a public read like any other; 200 of them must not be throttled.
        var throttled = 0;
        for (var i = 0; i < 200; i++)
        {
            using var response = await client.GetAsync("/api/sellers/someone");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled++;
            }
        }

        throttled.ShouldBe(0);
    }
}

/// <summary>The catalogue budget is large, not unlimited.</summary>
public sealed class CatalogueBudgetIsFiniteTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task The_catalogue_budget_is_still_a_budget()
    {
        using var client = factory.CreateClient();

        HttpResponseMessage? throttled = null;
        try
        {
            for (var i = 0; i < 900 && throttled is null; i++)
            {
                var response = await client.GetAsync($"/api/catalog/browse?q=unik{i}");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throttled = response;
                }
                else
                {
                    response.Dispose();
                }
            }

            throttled.ShouldNotBeNull("a script scraping the catalogue must still be cut off");
            throttled.Headers.RetryAfter.ShouldNotBeNull();
        }
        finally
        {
            throttled?.Dispose();
        }
    }
}
