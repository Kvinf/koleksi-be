namespace Koleksi.Infrastructure.IntegrationTests;

/// <summary>
/// A table of its own for every single test.
/// </summary>
/// <remarks>
/// <para>
/// Not a class fixture, and not a shared one. Facet counters are per-category and the
/// browse projection is per-category, so any two tests that index a card listing would
/// see each other's rows and each other's totals. Isolating at the class level would only
/// move that problem inside the class.
/// </para>
/// <para>
/// xUnit constructs a fresh instance of a test class for every test and awaits
/// <see cref="IAsyncLifetime.InitializeAsync"/> around it, so inheriting this is all a
/// suite needs to get a clean table. DynamoDB Local holds the table in memory, so the
/// create-and-drop costs milliseconds.
/// </para>
/// </remarks>
public abstract class DynamoTestBase : IAsyncLifetime
{
    /// <summary>The table for this test, and the production repositories wired to it.</summary>
    protected DynamoTableFixture Table { get; } = new();

    /// <summary>Nothing here cancels; the name keeps the call sites readable.</summary>
    protected static CancellationToken Ct => CancellationToken.None;

    public Task InitializeAsync() => Table.InitializeAsync();

    public Task DisposeAsync() => Table.DisposeAsync();
}
