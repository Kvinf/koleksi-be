using Shouldly;

namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary><c>DynamoRealtimeTicketStore</c> against a real table.</summary>
/// <remarks>
/// The property a fake cannot vouch for: redeeming is one conditional delete, so two
/// connections racing with the same ticket cannot both get in.
/// </remarks>
public sealed class RealtimeTicketStoreTests : DynamoTestBase
{
    private const string UserId = Fixtures.BidderId;

    [DynamoLocalFact]
    public async Task A_ticket_redeems_to_the_user_it_was_issued_to()
    {
        var ticket = await Table.RealtimeTickets.IssueAsync(UserId, Ct);

        (await Table.RealtimeTickets.ConsumeAsync(ticket, Ct)).ShouldBe(UserId);
    }

    [DynamoLocalFact]
    public async Task A_ticket_can_be_redeemed_only_once()
    {
        var ticket = await Table.RealtimeTickets.IssueAsync(UserId, Ct);

        (await Table.RealtimeTickets.ConsumeAsync(ticket, Ct)).ShouldBe(UserId);
        (await Table.RealtimeTickets.ConsumeAsync(ticket, Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Concurrent_redemptions_of_one_ticket_produce_exactly_one_winner()
    {
        var ticket = await Table.RealtimeTickets.IssueAsync(UserId, Ct);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Table.RealtimeTickets.ConsumeAsync(ticket, Ct)));

        attempts.Count(userId => userId is not null).ShouldBe(1);
    }

    [DynamoLocalFact]
    public async Task An_unknown_or_malformed_ticket_redeems_to_nothing()
    {
        (await Table.RealtimeTickets.ConsumeAsync("not-a-ticket", Ct)).ShouldBeNull();
        (await Table.RealtimeTickets.ConsumeAsync("", Ct)).ShouldBeNull();
        (await Table.RealtimeTickets.ConsumeAsync(null, Ct)).ShouldBeNull();
        (await Table.RealtimeTickets.ConsumeAsync(new string('x', 500), Ct)).ShouldBeNull();
    }

    [DynamoLocalFact]
    public async Task Tickets_are_unique_per_issue()
    {
        var first = await Table.RealtimeTickets.IssueAsync(UserId, Ct);
        var second = await Table.RealtimeTickets.IssueAsync(UserId, Ct);

        first.ShouldNotBe(second);
    }
}
