using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// A listing card arrives with a photo URL the browser can load as it is.
/// </summary>
/// <remarks>
/// Cards used to carry only the storage key, and the page rebuilt the URL from a variable baked
/// into the frontend at build time. A build made without that variable drew a grey placeholder on
/// every card while the item page, which uses the URL the API sends, kept working. Saying the URL
/// here is what stops a bad build from taking the photos away.
/// </remarks>
public sealed class PhotoUrlEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private const string PhotoKey = "listings/some-seller/some-photo";

    private static ListingSummary Card(string id, string? photoKey) => new(
        id, "Kartu Pokemon", TestData.CardsCategoryId, TestData.CardsCategorySlug,
        PriceRupiah: 150_000, ListingFormat.FixedPrice, ListingStatus.Active, "Mint",
        TestData.SellerUsername, "Jakarta", photoKey,
        AuctionEndsAt: null, BidCount: 0, SellerId: "seller-id-that-must-not-leak");

    [Fact]
    public async Task A_browse_card_carries_the_url_of_its_photo()
    {
        factory.SearchIndex.Seed(Card("with-photo", PhotoKey));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse");

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        var card = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "with-photo");

        card.GetProperty("primaryPhotoUrl").GetString().ShouldBe(factory.Media.PublicUrl(PhotoKey));
        card.GetProperty("primaryPhotoUrl").GetString().ShouldStartWith(FakeMediaStorage.CdnBase);

        // The key stays for anything that already reads it; the seller's account id never appears.
        card.GetProperty("primaryPhotoKey").GetString().ShouldBe(PhotoKey);
        card.TryGetProperty("sellerId", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_browse_card_without_a_photo_has_no_url()
    {
        factory.SearchIndex.Seed(Card("no-photo", photoKey: null));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/catalog/browse");

        var card = (await response.ReadJsonAsync()).GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "no-photo");

        card.TryGetProperty("primaryPhotoUrl", out _).ShouldBeFalse();
    }
}
