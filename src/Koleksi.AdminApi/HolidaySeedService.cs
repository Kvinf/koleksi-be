using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.AdminApi;

/// <summary>
/// Writes the 2026 official holidays the first time the admin API starts against a table that has none, and never again.
/// </summary>
/// <remarks>
/// <para>
/// A year already holding holidays is left alone (one cheap query), and the write itself is guarded by a marker row in the same
/// transaction (<see cref="IHolidayStore.SeedAsync"/>), so two Lambdas starting at once seed it once and a holiday staff removed
/// is not put back. The write is audited with the actor <c>system</c>.
/// </para>
/// <para>
/// A failure here never stops the API: the holiday list is something staff can fill in by hand, and the business-day count only
/// needs it for correctness, not to run. It is logged and the next cold start tries again.
/// </para>
/// </remarks>
internal sealed partial class HolidaySeedService(IHolidayStore store, IClock clock, ILogger<HolidaySeedService> logger) : IHostedService
{
    private const string SystemActor = "system";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Each year on its own: a year that is already there (or was seeded and then edited by staff) is left alone, and one year
        // failing does not stop the next.
        foreach (var (year, holidays) in HolidaySeed.ByYear)
        {
            try
            {
                if ((await store.ListAsync(year, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    continue;
                }

                var audit = AuditEntry.Create(
                    clock, SystemActor, SystemActor, AuditActions.HolidaySeeded, AuditTargets.Holiday, year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "startup",
                    after: $"{holidays.Count} holidays from the SKB 3 Menteri");

                if (await store.SeedAsync(year, holidays, audit, cancellationToken).ConfigureAwait(false))
                {
                    LogSeeded(logger, year, holidays.Count);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                LogSeedFailed(logger, failure);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the {Year} holidays ({Count}).")]
    private static partial void LogSeeded(ILogger logger, int year, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The holiday seed could not be written; it is tried again at the next start.")]
    private static partial void LogSeedFailed(ILogger logger, Exception exception);
}
