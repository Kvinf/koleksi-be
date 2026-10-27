#if DEBUG
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Users;

namespace Koleksi.AdminApi.Development;

/// <summary>
/// Creates a disputed order, a buyer, a seller and a little chat between them, so the dispute screens can be used on a developer's machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Disputes are opened by the marketplace's buyers, in the public API, after a payment, a shipment and a delivery.
/// None of that can happen from the admin site, and the admin API never writes those records for real. Without a way to produce one, the
/// dispute queue could only ever be seen empty.
/// </para>
/// <para>
/// <b>Why it cannot reach a deployed stage.</b> It is mapped only from <see cref="LocalStaffIdentity.MapEndpoints"/>, which is
/// itself reached only when all four of that type's locks hold, and it sits inside the same <c>#if DEBUG</c>. A Release build contains
/// neither type; <c>scripts/verify-release-has-no-local-staff.ps1</c> searches the Release output for this one's name too.
/// </para>
/// <para>
/// It writes ordinary order, profile and conversation rows through the same repositories the public API uses, so what the dispute screens
/// then read is exactly what a real dispute would leave. The two profiles carry fixed, obviously fake handles.
/// </para>
/// </remarks>
internal static class DevDisputeFixture
{
    private const string BuyerId = "dev-buyer-0000-0000-0000-000000000001";
    private const string SellerId = "dev-seller-000-0000-0000-000000000002";
    private const string BuyerHandle = "pembelidev";
    private const string SellerHandle = "penjualdev";


    public static void Map(IEndpointRouteBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // POST /dev/disputes/seed?openedDaysAgo=5 : how long ago the buyer opened the dispute, in calendar days (0..60).
        group.MapPost("/disputes/seed", async (
            int? openedDaysAgo,
            IOrderRepository orders,
            IPublicProfileRepository profiles,
            IConversationRepository conversations,
            CancellationToken ct) =>
        {
            var daysAgo = Math.Clamp(openedDaysAgo ?? 0, 0, 60);

            // A listing of its own per seeded case, so each case has its own chat and none shows another's.
            var listingId = "dev-listing-" + Guid.NewGuid().ToString("N")[..10];
            var clock = new SteppingClock(DateTimeOffset.UtcNow.AddDays(-daysAgo - 3));

            await EnsureProfileAsync(profiles, BuyerId, BuyerHandle, clock, ct).ConfigureAwait(false);
            await EnsureProfileAsync(profiles, SellerId, SellerHandle, clock, ct).ConfigureAwait(false);

            // A paid, shipped and delivered order, then the buyer's dispute: the only states a dispute can follow.
            var order = Order.Create(
                BuyerId, SellerId,
                [new OrderLine(listingId, "PSA 10 Charizard VMAX", null, Money.FromRupiah(4_250_000), 1)],
                new ShippingAddress("Dwi Pembeli", "081234567890", "Jl. Tebet Barat Dalam 12", "Jakarta Selatan", "DKI Jakarta", "12810"),
                new CourierSelection("jne", "JNE REG", Money.FromRupiah(24_000), 3),
                clock);
            order.ConfirmPayment(order.GrandTotal, clock.Step());
            order.MarkShipped(
                Shipment.Create("jne", "JNE" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999), "p.jpg", "l.jpg", DateOnly.FromDateTime(clock.Step().UtcNow.UtcDateTime)),
                SellerId, clock);
            order.MarkDelivered(clock.Step());
            // Saved delivered, then disputed and saved again, as the public API does it, so the dispute gets its per-person pointers.
            await orders.AddManyAsync([order], ct).ConfigureAwait(false);
            var versionBeforeDispute = order.Version;
            clock.JumpTo(DateTimeOffset.UtcNow.AddDays(-daysAgo));
            order.OpenDispute("Kartu yang datang bukan PSA 10 seperti di deskripsi, slab-nya palsu.", BuyerId, clock);
            await orders.UpdateAsync(order, versionBeforeDispute, ct).ConfigureAwait(false);

            // The chat the order came out of.
            var conversation = Conversation.Start(listingId, "PSA 10 Charizard VMAX", null, BuyerId, BuyerHandle, SellerId, SellerHandle, clock);
            foreach (var (sender, text) in new[]
            {
                (BuyerId, "Halo kak, slab-nya asli PSA 10 ya? Boleh minta foto sertifikatnya?"),
                (SellerId, "Asli kak, ini sertifikat nomor 12345678, bisa dicek di situs PSA."),
                (BuyerId, "Oke, saya ambil."),
            })
            {
                var message = ChatMessage.Create(conversation.Id, sender, text, clock.Step());
                conversation.RecordMessage(message);
                await conversations.AppendAsync(conversation, message, ct).ConfigureAwait(false);
            }

            return Results.Ok(new { orderId = order.Id, orderNumber = order.OrderNumber, openedAt = order.Dispute?.OpenedAt });
        }).AllowAnonymous();
    }

    private static async Task EnsureProfileAsync(
        IPublicProfileRepository profiles, string userId, string handle, IClock clock, CancellationToken ct)
    {
        if (await profiles.FindByUserAsync(userId, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        await profiles.CreateAsync(PublicProfile.Create(userId, handle, clock.UtcNow, clock), ct).ConfigureAwait(false);
    }

    /// <summary>A clock that starts in the past and moves forward a minute each time it is stepped, so records have a believable order.</summary>
    private sealed class SteppingClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public SteppingClock Step()
        {
            UtcNow += TimeSpan.FromMinutes(7);
            return this;
        }

        public void JumpTo(DateTimeOffset instant) => UtcNow = instant;
    }
}
#endif
