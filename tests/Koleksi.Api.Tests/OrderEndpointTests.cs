using System.Net;
using System.Net.Http.Json;
using Koleksi.Api.Tests.Fakes;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Catalog;
using Koleksi.Domain.Common;
using Koleksi.Domain.Offers;
using Koleksi.Domain.Orders;
using Shouldly;

namespace Koleksi.Api.Tests;

/// <summary>Checkout, fulfilment, and the payment gateway's own webhook, end to end.</summary>
public sealed class OrderEndpointTests(KoleksiApiFactory factory) : IClassFixture<KoleksiApiFactory>
{
    [Fact]
    public async Task Checking_out_a_fixed_price_listing_creates_an_order_and_starts_payment()
    {
        var listing = SeedFixedPriceListing();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("paymentUnavailable").GetBoolean().ShouldBeFalse();
        body.GetProperty("paymentRedirectUrl").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("grandTotalRupiah").GetInt64().ShouldBe(listing.Price.Rupiah + 15_000);
    }

    [Fact]
    public async Task Checkout_still_creates_the_order_when_the_payment_gateway_is_unavailable()
    {
        var listing = SeedFixedPriceListing();
        factory.Payments.CreateSessionFailsWith = DomainException.For(
            "payment.not_configured", "Payment gateway credentials are not configured for this environment.");
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        try
        {
            using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await response.ReadJsonAsync();
            body.GetProperty("paymentUnavailable").GetBoolean().ShouldBeTrue();
            body.GetProperty("orderId").GetString().ShouldNotBeNullOrEmpty();
        }
        finally
        {
            factory.Payments.CreateSessionFailsWith = null;
        }
    }

    [Fact]
    public async Task Checkout_for_an_accepted_offer_uses_the_agreed_price_not_the_asking_price()
    {
        var listing = SeedNegoListing();
        var offer = Offer.Create(
            listing.Id, TestData.BidderId, listing.SellerId, Money.FromRupiah(2_000_000), listing.Price,
            "Bisa kurang?", autoDeclineBelowPercent: null, new FixedTestClock());
        offer.Accept(listing.SellerId, new FixedTestClock());
        factory.Offers.Seed(offer);

        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("AcceptedOffer", offer.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("grandTotalRupiah").GetInt64().ShouldBe(2_000_000 + 15_000);
    }

    [Fact]
    public async Task A_stranger_to_the_order_gets_the_same_not_found_a_bad_id_would()
    {
        var order = SeedPaidOrder();
        using var client = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        using var response = await client.GetAsync($"/api/orders/{order.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_found");
    }

    [Fact]
    public async Task The_buyer_and_seller_each_see_their_own_role_on_the_order()
    {
        var order = SeedPaidOrder();

        using var buyerClient = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var buyerResponse = await buyerClient.GetAsync($"/api/orders/{order.Id}");
        buyerResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await buyerResponse.ReadJsonAsync()).GetProperty("viewerRole").GetString().ShouldBe("buyer");

        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var sellerResponse = await sellerClient.GetAsync($"/api/orders/{order.Id}");
        sellerResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sellerResponse.ReadJsonAsync()).GetProperty("viewerRole").GetString().ShouldBe("seller");
    }

    [Fact]
    public async Task An_unpaid_order_cannot_be_marked_shipped()
    {
        var order = SeedAwaitingPaymentOrder();
        using var client = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_shippable");
    }

    [Fact]
    public async Task The_seller_can_mark_a_paid_order_shipped_and_the_buyer_can_then_confirm_delivery()
    {
        var order = SeedPaidOrder();
        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var shipResponse = await sellerClient.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId));

        shipResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await shipResponse.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Shipped");

        // Courier-confirmed delivery has no client-facing endpoint yet — it is meant to
        // come from a tracking webhook that does not exist in this environment (the same
        // documented gap as the auto-close sweep). Applied directly here to reach the
        // state /release actually requires.
        var shippedOrder = await factory.Orders.FindAsync(order.Id, CancellationToken.None);
        shippedOrder.ShouldNotBeNull();
        shippedOrder.MarkDelivered(new FixedTestClock());
        await factory.Orders.UpdateAsync(shippedOrder, shippedOrder.Version - 1, CancellationToken.None);

        using var buyerClient = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var releaseResponse = await buyerClient.PostAsync($"/api/orders/{order.Id}/release", null);

        releaseResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await releaseResponse.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Released");
    }

    [Fact]
    public async Task The_seller_cannot_confirm_their_own_delivery()
    {
        var order = SeedPaidOrder();
        using var sellerClient = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await sellerClient.PostAsync($"/api/orders/{order.Id}/release", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.forbidden");
    }

    [Fact]
    public async Task A_buyer_can_open_a_dispute_on_a_paid_order()
    {
        var order = SeedPaidOrder();
        using var client = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await client.PostAsJsonAsync(
            $"/api/orders/{order.Id}/dispute", new { reason = "Barang tidak sesuai deskripsi." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("escrowState").GetString().ShouldBe("Disputed");
    }

    [Fact]
    public async Task An_unverified_webhook_notification_is_rejected()
    {
        factory.Payments.NextNotification = null;
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/payments/webhook", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_settled_webhook_notification_pays_the_order_and_marks_the_listing_sold()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        factory.Orders.Seed(order);

        factory.Payments.NextNotification = new PaymentNotification(
            order.OrderNumber, PaymentStatus.Settled, order.GrandTotal, "PROC-1", $"evt-{order.Id}");

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/payments/webhook", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updatedOrder = await factory.Orders.FindAsync(order.Id, CancellationToken.None);
        updatedOrder.ShouldNotBeNull();
        updatedOrder.EscrowState.ShouldBe(EscrowState.Paid);

        var updatedListing = await factory.Listings.FindAsync(listing.Id, CancellationToken.None);
        updatedListing.ShouldNotBeNull();
        updatedListing.Status.ShouldBe(ListingStatus.Sold);
    }

    // ─── No account ids on the wire ──────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex AccountIdShape = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static void ShouldCarryNoAccountIdentity(string wire)
    {
        foreach (var id in new[] { TestData.SellerId, TestData.BidderId, TestData.RivalId })
        {
            wire.ShouldNotContain(id, Case.Insensitive);
        }

        wire.ShouldNotContain("@");
        AccountIdShape.IsMatch(wire).ShouldBeFalse("a Cognito-subject-shaped value is on the wire");
        wire.ShouldNotContain("buyerId", Case.Insensitive);
        wire.ShouldNotContain("sellerId", Case.Insensitive);
    }

    [Fact]
    public async Task Order_lists_and_detail_name_the_parties_by_public_handle_and_never_by_account_id()
    {
        var order = SeedPaidOrder();
        var buyerHandle = factory.PublicHandleOf(TestData.BidderId);
        var sellerHandle = factory.PublicHandleOf(TestData.SellerId);

        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        foreach (var (client, path, role) in new[]
        {
            (buyer, "/api/orders/mine", "buyer"),
            (seller, "/api/orders/selling", "seller"),
        })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var text = await response.ReadTextAsync();
            ShouldCarryNoAccountIdentity(text);

            var row = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("items")
                .EnumerateArray().Single(item => item.GetProperty("id").GetString() == order.Id);
            row.GetProperty("viewerRole").GetString().ShouldBe(role);
            row.GetProperty("buyerHandle").GetString().ShouldBe(buyerHandle);
            row.GetProperty("sellerHandle").GetString().ShouldBe(sellerHandle);
        }

        foreach (var (client, role) in new[] { (buyer, "buyer"), (seller, "seller") })
        {
            using var response = await client.GetAsync($"/api/orders/{order.Id}");
            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            var text = await response.ReadTextAsync();
            ShouldCarryNoAccountIdentity(text);

            var body = System.Text.Json.JsonDocument.Parse(text).RootElement;
            body.GetProperty("viewerRole").GetString().ShouldBe(role);
            body.GetProperty("order").GetProperty("viewerRole").GetString().ShouldBe(role);
            body.GetProperty("order").GetProperty("buyerHandle").GetString().ShouldBe(buyerHandle);
            body.GetProperty("order").GetProperty("sellerHandle").GetString().ShouldBe(sellerHandle);
        }
    }

    [Fact]
    public async Task A_checkout_response_carries_no_account_id_or_email()
    {
        var listing = SeedFixedPriceListing();
        using var client = factory.CreateClientAs(
            "b8a6f7d2-4c11-4a39-9a0e-0e5a3c1d7f01", "checkout.leakcheck");

        using var response = await client.PostAsJsonAsync("/api/orders/checkout", CheckoutBody("FixedPrice", listing.Id));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldCarryNoAccountIdentity(await response.ReadTextAsync());
    }

    // ─── A stranger is told "not found", exactly as for a missing order ──────────

    [Fact]
    public async Task A_stranger_shipping_releasing_or_disputing_gets_the_same_answer_a_missing_order_gets()
    {
        var order = SeedPaidOrder();
        var missingId = TestData.UnknownListingId;
        using var stranger = factory.CreateClientAs(TestData.RivalId, TestData.RivalUsername);

        var attempts = new (string Name, Func<string, Task<HttpResponseMessage>> Send)[]
        {
            ("ship", id => stranger.PostAsJsonAsync($"/api/orders/{id}/shipment", ShipBody(TestData.RivalId))),
            ("release", id => stranger.PostAsync($"/api/orders/{id}/release", null)),
            ("dispute", id => stranger.PostAsJsonAsync($"/api/orders/{id}/dispute", new { reason = "Saya ingin tahu." })),
        };

        foreach (var (name, send) in attempts)
        {
            using var real = await send(order.Id);
            using var missing = await send(missingId);

            real.StatusCode.ShouldBe(HttpStatusCode.NotFound, name);
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound, name);
            (await real.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.not_found", name);
            (await real.ReadTextAsync()).ShouldBe(await missing.ReadTextAsync(), name);
        }

        // And nothing happened to the order.
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);
    }

    [Fact]
    public async Task A_party_in_the_wrong_role_keeps_the_coded_refusal()
    {
        var order = SeedPaidOrder();

        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);
        using var buyerShips = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/shipment", ShipBody(TestData.BidderId));
        buyerShips.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await buyerShips.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.forbidden");

        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        using var sellerDisputes = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/dispute", new { reason = "Saya penjual." });
        sellerDisputes.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await sellerDisputes.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.forbidden");
    }

    // ─── Disputes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dispute_cannot_be_opened_twice_and_the_first_reason_is_kept()
    {
        var order = SeedPaidOrder();
        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var first = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/dispute", new { reason = "Alasan pertama." });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var second = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/dispute", new { reason = "Alasan kedua." });
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await second.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.already_disputed");

        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.Dispute!.Reason.ShouldBe("Alasan pertama.");
    }

    [Theory]
    [InlineData("", "order.dispute_reason_required")]
    [InlineData("   ", "order.dispute_reason_required")]
    public async Task A_blank_dispute_reason_is_refused(string reason, string code)
    {
        var order = SeedPaidOrder();
        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var response = await buyer.PostAsJsonAsync($"/api/orders/{order.Id}/dispute", new { reason });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_dispute_reason_is_capped_at_one_thousand_characters()
    {
        var order = SeedPaidOrder();
        using var buyer = factory.CreateClientAs(TestData.BidderId, TestData.BidderUsername);

        using var tooLong = await buyer.PostAsJsonAsync(
            $"/api/orders/{order.Id}/dispute", new { reason = new string('x', Dispute.MaxReasonLength + 1) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLong.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("order.dispute_reason_too_long");

        using var atTheLimit = await buyer.PostAsJsonAsync(
            $"/api/orders/{order.Id}/dispute", new { reason = new string('x', Dispute.MaxReasonLength) });
        atTheLimit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Shipment inputs ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", "shipment.tracking_too_short")]
    [InlineData("   ", "shipment.tracking_too_short")]
    [InlineData("SHORT", "shipment.tracking_too_short")]
    public async Task A_missing_or_short_tracking_number_is_refused(string tracking, string code)
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment", ShipBody(TestData.SellerId, trackingNumber: tracking));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task A_tracking_number_is_capped_at_sixty_four_characters()
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var tooLong = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId, trackingNumber: new string('9', Shipment.MaxTrackingNumberLength + 1)));

        tooLong.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await tooLong.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("shipment.tracking_too_long");

        using var atTheLimit = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId, trackingNumber: new string('9', Shipment.MaxTrackingNumberLength)));
        atTheLimit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_shipping_date_in_the_future_is_refused_but_a_timezone_ahead_is_not()
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        var today = DateOnly.FromDateTime(factory.Clock.UtcNow.UtcDateTime);

        using var future = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId, shippedOn: today.AddDays(2).ToString("yyyy-MM-dd")));
        future.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await future.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("shipment.shipped_on_future");

        using var tomorrowInJakarta = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            ShipBody(TestData.SellerId, shippedOn: today.AddDays(1).ToString("yyyy-MM-dd")));
        tomorrowInJakarta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Shipment_proof_must_be_the_sellers_own_shipping_upload_that_exists()
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        var mine = TestData.MediaKey(MediaPurpose.ShippingProof, TestData.SellerId, "mine");
        var someoneElses = TestData.MediaKey(MediaPurpose.ShippingProof, TestData.RivalId, "theirs");
        var myAvatar = TestData.MediaKey(MediaPurpose.Avatar, TestData.SellerId, "me.jpg");
        var myListingPhoto = TestData.PhotoKey(TestData.SellerId, "photo");

        foreach (var bad in new[] { someoneElses, myAvatar, myListingPhoto, "shipping/../x/y", "k1" })
        {
            using var response = await seller.PostAsJsonAsync(
                $"/api/orders/{order.Id}/shipment", ShipBody(TestData.SellerId, packedKey: bad, labelKey: mine));
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, bad);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("shipment.proof_not_uploaded", bad);

            using var label = await seller.PostAsJsonAsync(
                $"/api/orders/{order.Id}/shipment", ShipBody(TestData.SellerId, packedKey: mine, labelKey: bad));
            label.StatusCode.ShouldBe(HttpStatusCode.Conflict, bad);
        }

        // Nothing was saved by the refusals.
        (await factory.Orders.FindAsync(order.Id, CancellationToken.None))!.EscrowState.ShouldBe(EscrowState.Paid);

        // A key that is the seller's own but was never actually uploaded is refused too.
        factory.Media.NextVerifyUploadedResult = false;
        try
        {
            using var notUploaded = await seller.PostAsJsonAsync(
                $"/api/orders/{order.Id}/shipment", ShipBody(TestData.SellerId));
            notUploaded.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await notUploaded.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("shipment.proof_not_uploaded");
        }
        finally
        {
            factory.Media.NextVerifyUploadedResult = true;
        }

        using var ok = await seller.PostAsJsonAsync($"/api/orders/{order.Id}/shipment", ShipBody(TestData.SellerId));
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_proof_keys_are_a_bad_request()
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);

        using var response = await seller.PostAsJsonAsync(
            $"/api/orders/{order.Id}/shipment",
            new { trackingNumber = "JX1234567890", shippedOn = "2026-01-05" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("shipment.proof_required");
    }

    [Fact]
    public async Task A_double_submitted_shipment_replays_the_first_answer_when_it_carries_a_key()
    {
        var order = SeedPaidOrder();
        using var seller = factory.CreateClientAs(TestData.SellerId, TestData.SellerUsername);
        var key = Guid.NewGuid().ToString();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/orders/{order.Id}/shipment")
            {
                Content = JsonContent.Create(ShipBody(TestData.SellerId)),
            };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await seller.SendAsync(request);

            // Without the key the second tap would be a 409 order.not_shippable.
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"attempt {attempt}");
        }
    }

    // ─── Checkout input ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_checkout_without_a_ship_to_is_a_bad_request_not_a_server_error()
    {
        var listing = SeedFixedPriceListing();
        using var client = factory.CreateClientAs("c1a6f7d2-4c11-4a39-9a0e-0e5a3c1d7f02", "checkout.noshipto");

        using var response = await client.PostAsJsonAsync(
            "/api/orders/checkout", new { source = "FixedPrice", referenceId = listing.Id, courierCode = "jne" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("checkout.invalid_details");
    }

    [Fact]
    public async Task A_checkout_with_a_blank_or_oversized_address_field_is_refused()
    {
        var listing = SeedFixedPriceListing();
        using var client = factory.CreateClientAs("c2a6f7d2-4c11-4a39-9a0e-0e5a3c1d7f03", "checkout.badaddress");

        foreach (var shipTo in new object[]
        {
            new { recipientName = "", phone = "081234567890", street = "Jl. Merdeka", city = "Bandung", province = "Jawa Barat", postalCode = "40111" },
            new { recipientName = "Rangga", phone = "081234567890", street = "   ", city = "Bandung", province = "Jawa Barat", postalCode = "40111" },
            new { recipientName = "Rangga", phone = "081234567890", street = "Jl. Merdeka", city = "Bandung", province = "Jawa Barat" },
            new { recipientName = new string('x', 101), phone = "081234567890", street = "Jl. Merdeka", city = "Bandung", province = "Jawa Barat", postalCode = "40111" },
            new { recipientName = "Rangga", phone = new string('1', 21), street = "Jl. Merdeka", city = "Bandung", province = "Jawa Barat", postalCode = "40111" },
            new { recipientName = "Rangga", phone = "081234567890", street = new string('x', 241), city = "Bandung", province = "Jawa Barat", postalCode = "40111" },
        })
        {
            using var response = await client.PostAsJsonAsync(
                "/api/orders/checkout", new { source = "FixedPrice", referenceId = listing.Id, shipTo, courierCode = "jne" });

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("checkout.invalid_details");
        }
    }

    // ─── The payment webhook ─────────────────────────────────────────────────────

    [Fact]
    public async Task An_oversized_webhook_body_is_refused_before_any_signature_work()
    {
        factory.Payments.NextNotification = null;
        using var client = factory.CreateClient();

        using var content = new StringContent(new string('a', 64 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/payments/webhook", content);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("payment.payload_too_large");
    }

    [Fact]
    public async Task A_webhook_body_at_the_limit_still_reaches_signature_verification()
    {
        factory.Payments.NextNotification = null;
        using var client = factory.CreateClient();

        using var content = new StringContent(new string('a', 64 * 1024), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/payments/webhook", content);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static object ShipBody(
        string ownerId,
        string trackingNumber = "JX1234567890",
        string shippedOn = "2026-01-05",
        string? packedKey = null,
        string? labelKey = null) => new
    {
        trackingNumber,
        packedParcelPhotoKey = packedKey ?? TestData.MediaKey(MediaPurpose.ShippingProof, ownerId, "packed"),
        trackingLabelPhotoKey = labelKey ?? TestData.MediaKey(MediaPurpose.ShippingProof, ownerId, "label"),
        shippedOn,
    };

    private static object CheckoutBody(string source, string referenceId) => new
    {
        source,
        referenceId,
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
    };

    private static ShippingAddress SampleShippingAddress() =>
        new("Rangga Wijaya", "081234567890", "Jl. Merdeka No. 1", "Bandung", "Jawa Barat", "40111");

    private static CourierSelection FlatRateCourier() => Koleksi.Application.Features.Orders.FlatRateCouriers.Find("jne")!;

    private Listing SeedFixedPriceListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000));
        factory.Listings.Seed(listing);
        return listing;
    }

    private Listing SeedNegoListing()
    {
        var listing = TestData.ActiveListing(price: Money.FromRupiah(3_000_000), format: ListingFormat.AcceptOffers);
        factory.Listings.Seed(listing);
        return listing;
    }

    private Order SeedAwaitingPaymentOrder()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        factory.Orders.Seed(order);
        return order;
    }

    private Order SeedPaidOrder()
    {
        var listing = SeedFixedPriceListing();
        var order = Order.Create(
            TestData.BidderId, listing.SellerId,
            [new OrderLine(listing.Id, listing.Title, null, listing.Price, Quantity: 1)],
            SampleShippingAddress(), FlatRateCourier(), new FixedTestClock());
        order.ConfirmPayment(order.GrandTotal, new FixedTestClock());
        factory.Orders.Seed(order);
        return order;
    }

    private sealed class FixedTestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = MutableClock.Origin;
    }
}
