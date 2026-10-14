using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Koleksi.Application.Abstractions;
using Koleksi.Domain.Common;
using Koleksi.Domain.Messaging;
using Koleksi.Domain.Orders;
using Koleksi.Domain.Reviews;
using Koleksi.Domain.Users;
using Koleksi.Infrastructure.Persistence;
using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// A transaction cancelled for <c>TransactionConflict</c> is an optimistic-concurrency loss, not a fault.
/// </summary>
/// <remarks>
/// <para>
/// When two transactions touch the same item at the same instant DynamoDB cancels one of them with
/// the reason <c>TransactionConflict</c>, before any condition is evaluated. The repositories only
/// recognised <c>ConditionalCheckFailed</c>, so the loser surfaced as an unhandled 500. DynamoDB Local
/// cannot be made to produce that reason on demand, so these tests hand each repository a client
/// whose transaction is cancelled with exactly the reasons a real conflict reports.
/// </para>
/// <para>
/// They need no DynamoDB Local and always run. The client is a <see cref="DispatchProxy"/>, so it
/// implements the whole SDK interface without a mocking package; any call other than the
/// transaction fails the test, which also pins that these write paths do nothing else first.
/// </para>
/// </remarks>
public sealed class TransactionConflictTests
{
    private static readonly DynamoOptions Options = new() { TableName = "koleksi-test" };

    private const string Buyer = Fixtures.BidderId;
    private const string Seller = Fixtures.SellerId;

    // What DynamoDB reports for a transaction of three writes that lost to another transaction.
    private static readonly string[] Conflict = ["TransactionConflict", "None", "None"];

    // What it reports when the second write's condition failed.
    private static readonly string[] ConditionFailed = ["None", "ConditionalCheckFailed", "None"];

    private static IAmazonDynamoDB Cancelled(params string[] reasons)
    {
        var client = DispatchProxy.Create<IAmazonDynamoDB, CancelledTransactionClient>();
        ((CancelledTransactionClient)(object)client).Reasons = reasons;
        return client;
    }

    // ---- The shared decision --------------------------------------------------------------

    [Theory]
    [InlineData("TransactionConflict", true)]
    [InlineData("ConditionalCheckFailed", true)]
    [InlineData("ThrottlingError", false)]
    [InlineData("ProvisionedThroughputExceeded", false)]
    [InlineData("ValidationError", false)]
    [InlineData("None", false)]
    public void Only_a_lost_race_counts_as_a_conflict(string reason, bool expected)
    {
        var cancelled = new TransactionCanceledException("cancelled")
        {
            CancellationReasons = [new CancellationReason { Code = "None" }, new CancellationReason { Code = reason }],
        };

        DynamoTransactionConflicts.IsConflict(cancelled).ShouldBe(expected);
    }

    [Fact]
    public void A_cancellation_with_no_reasons_is_not_a_conflict()
    {
        DynamoTransactionConflicts.IsConflict(new TransactionCanceledException("cancelled")).ShouldBeFalse();
    }

    // ---- Each repository path -------------------------------------------------------------

    [Fact]
    public async Task A_bid_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.Auction);
        var auction = Fixtures.LiveAuction(listing);
        var expected = auction.Version;
        var outcome = auction.PlaceBid(Buyer, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        var repository = new DynamoAuctionRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.SaveBidAsync(auction, outcome.Bids, expected, CancellationToken.None));
    }

    [Fact]
    public async Task A_bid_whose_condition_failed_is_still_a_concurrency_conflict()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.Auction);
        var auction = Fixtures.LiveAuction(listing);
        var expected = auction.Version;
        var outcome = auction.PlaceBid(Buyer, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        var repository = new DynamoAuctionRepository(Cancelled(ConditionFailed), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.SaveBidAsync(auction, outcome.Bids, expected, CancellationToken.None));
    }

    [Fact]
    public async Task A_bid_cancelled_for_an_unrelated_reason_is_not_hidden()
    {
        var listing = Fixtures.ActiveListing(format: Koleksi.Domain.Catalog.ListingFormat.Auction);
        var auction = Fixtures.LiveAuction(listing);
        var expected = auction.Version;
        var outcome = auction.PlaceBid(Buyer, Fixtures.BidderUsername, Money.FromRupiah(4_000_000), Fixtures.Clock);
        var repository = new DynamoAuctionRepository(Cancelled("ValidationError", "None"), Options);

        await Should.ThrowAsync<TransactionCanceledException>(
            () => repository.SaveBidAsync(auction, outcome.Bids, expected, CancellationToken.None));
    }

    [Fact]
    public async Task Adding_an_offer_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var offer = Fixtures.PendingOffer(Fixtures.ActiveListing());
        var repository = new DynamoOfferRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.AddAsync(offer, CancellationToken.None));
    }

    [Fact]
    public async Task Updating_an_offer_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var offer = Fixtures.PendingOffer(Fixtures.ActiveListing());
        var expected = offer.Version;
        offer.Withdraw(Buyer, Fixtures.Clock);
        var repository = new DynamoOfferRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.UpdateAsync(offer, expected, CancellationToken.None));
    }

    [Fact]
    public async Task A_checkout_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var order = Fixtures.SampleOrder(Fixtures.ActiveListing());
        var repository = new DynamoOrderRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.AddManyAsync([order], CancellationToken.None));
    }

    [Fact]
    public async Task A_message_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var conversation = Conversation.Start(
            "01JQ8ZC4K7N2M5V9XR3P6TW0YB", "PSA 10 Charizard", "listings/a/b", Buyer, "rangga.wh", Seller, "budi",
            Fixtures.Clock);
        var message = ChatMessage.Create(conversation.Id, Buyer, "Masih ada kak?", Fixtures.Clock);
        conversation.RecordMessage(message);
        var repository = new DynamoConversationRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.AppendAsync(conversation, message, CancellationToken.None));
    }

    [Fact]
    public async Task A_review_that_collides_with_another_transaction_is_a_concurrency_conflict_not_already_reviewed()
    {
        // Another review of this seller is moving the same rating totals. That says nothing about
        // whether this order was reviewed, so answering "false" (already reviewed) would be a lie.
        var review = Review.Create(DeliveredOrder(), Buyer, 5, "Mantap", false, Fixtures.Clock);
        var repository = new DynamoReviewRepository(Cancelled(Conflict), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.TryAddAsync(review, CancellationToken.None));
    }

    [Fact]
    public async Task A_review_of_an_order_already_reviewed_is_still_reported_as_such()
    {
        var review = Review.Create(DeliveredOrder(), Buyer, 5, "Mantap", false, Fixtures.Clock);
        var repository = new DynamoReviewRepository(Cancelled("ConditionalCheckFailed", "None", "None"), Options);

        (await repository.TryAddAsync(review, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Creating_a_public_profile_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var profile = PublicProfile.Create(Seller, "budi", Fixtures.Clock.UtcNow, Fixtures.Clock);
        var repository = new DynamoPublicProfileRepository(Cancelled("TransactionConflict", "None"), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.CreateAsync(profile, CancellationToken.None));
    }

    [Fact]
    public async Task Saving_a_public_profile_that_collides_with_another_transaction_is_a_concurrency_conflict()
    {
        var profile = PublicProfile.Create(Seller, "budi", Fixtures.Clock.UtcNow, Fixtures.Clock);
        var expected = profile.Version;
        var repository = new DynamoPublicProfileRepository(Cancelled("TransactionConflict", "None"), Options);

        await Should.ThrowAsync<ConcurrencyConflictException>(
            () => repository.SaveAsync(profile, "budi", expected, CancellationToken.None));
    }

    private static Order DeliveredOrder()
    {
        var order = Order.Create(
            Buyer,
            Seller,
            [new OrderLine("01JQ8ZC4K7N2M5V9XR3P6TW0YB", "PSA 10 Charizard", null, Money.FromRupiah(2_500_000), 1)],
            new ShippingAddress("Dwi", "0812", "Jl. Tebet", "Jakarta Selatan", "DKI Jakarta", "12810"),
            new CourierSelection("jne", "JNE REG", Money.FromRupiah(15_000), 3),
            Fixtures.Clock);
        order.ConfirmPayment(order.GrandTotal, Fixtures.Clock);
        order.MarkShipped(
            Shipment.Create("jne", "JNE0012345678", "p/packed.jpg", "p/label.jpg", new DateOnly(2026, 1, 1)),
            Seller,
            Fixtures.Clock);
        order.MarkDelivered(Fixtures.Clock);
        return order;
    }

    /// <summary>
    /// A DynamoDB client whose only supported call is a transaction, and which cancels it.
    /// </summary>
    public class CancelledTransactionClient : DispatchProxy
    {
        public string[] Reasons { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IAmazonDynamoDB.TransactWriteItemsAsync))
            {
                return Task.FromException<TransactWriteItemsResponse>(
                    new TransactionCanceledException("Transaction cancelled, please refer cancellation reasons")
                    {
                        CancellationReasons = Reasons.Select(code => new CancellationReason { Code = code }).ToList(),
                    });
            }

            if (targetMethod.Name == nameof(IDisposable.Dispose))
            {
                return null;
            }

            throw new NotSupportedException(
                $"These tests expect the repository to go straight to the transaction, not call {targetMethod.Name}.");
        }
    }
}
