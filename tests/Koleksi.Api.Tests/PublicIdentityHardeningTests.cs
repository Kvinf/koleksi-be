using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Domain.Users;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>
/// A renamed shop keeps its old handle reserved, and a display name cannot pose as the platform
/// (QA D-10). Its own class (so its own host and clock): one test moves time forward 90 days.
/// </summary>
public sealed class PublicIdentityHardeningTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    private static string NewHandle() => "toko-" + Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("Koleksi Official")]
    [InlineData("Koleksi.id")]
    [InlineData("KOLEKSI")]
    [InlineData("koleksi resmi")]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("a d m i n")]
    [InlineData("staff")]
    [InlineData("Staff Koleksi")]
    [InlineData("Official Store")]
    [InlineData("Customer Support")]
    [InlineData("Moderator")]
    public async Task A_display_name_that_poses_as_the_platform_or_its_staff_is_refused(string displayName)
    {
        using var seller = factory.CreateClientAs(Guid.NewGuid().ToString(), "impersonator@mail.test");

        using var response = await seller.PutAsJsonAsync("/api/profile/public", new { handle = NewHandle(), displayName });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("profile.display_name_invalid");
    }

    [Theory]
    [InlineData("Toko Kevin")]
    [InlineData("Gunpla Garage Bandung")]
    [InlineData("Budi Kartu Langka")]
    public async Task An_ordinary_display_name_is_still_accepted(string displayName)
    {
        using var seller = factory.CreateClientAs(Guid.NewGuid().ToString(), "ordinary@mail.test");

        using var response = await seller.PutAsJsonAsync("/api/profile/public", new { handle = NewHandle(), displayName });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_handle_left_by_a_rename_is_reserved_for_ninety_days_and_then_free()
    {
        var ownerId = Guid.NewGuid().ToString();
        var oldHandle = factory.PublicHandleOf(ownerId);
        using var owner = factory.CreateClientAs(ownerId, "owner@mail.test");

        var newHandle = NewHandle();
        using var rename = await owner.PutAsJsonAsync("/api/profile/public", new { handle = newHandle });
        rename.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The old shop URL no longer resolves, but nobody else can take the name yet.
        using var visitor = factory.CreateClient();
        (await visitor.GetAsync($"/api/sellers/{oldHandle}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var squatterId = Guid.NewGuid().ToString();
        factory.PublicHandleOf(squatterId);
        using var squatter = factory.CreateClientAs(squatterId, "squatter@mail.test");

        using var tooSoon = await squatter.PutAsJsonAsync("/api/profile/public", new { handle = oldHandle });
        tooSoon.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooSoon.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("handle.taken");

        factory.Clock.Advance(PublicProfile.HandleReservationPeriod - TimeSpan.FromDays(1));
        using var almost = await squatter.PutAsJsonAsync("/api/profile/public", new { handle = oldHandle });
        almost.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        factory.Clock.Advance(TimeSpan.FromDays(2));
        using var afterwards = await squatter.PutAsJsonAsync("/api/profile/public", new { handle = oldHandle });
        afterwards.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void The_reservation_period_is_ninety_days()
    {
        PublicProfile.HandleReservationPeriod.ShouldBe(TimeSpan.FromDays(90));
    }
}
