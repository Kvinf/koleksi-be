using Koleksi.Application.Abstractions;
using Koleksi.Domain.Admin;
using Koleksi.Domain.Common;

namespace Koleksi.Application.Features.Admin;

public sealed record AddHolidayRequest(string? Date, string? Name, string? Kind);

internal static class HolidayWire
{
    public static string Kind(HolidayKind kind) => kind == HolidayKind.National ? "national" : "collectiveLeave";

    public static HolidayKind ParseKind(string? value) =>
        value switch
        {
            "national" => HolidayKind.National,
            "collectiveLeave" => HolidayKind.CollectiveLeave,
            _ => throw DomainException.For("admin.holiday_invalid", "The kind is national or collectiveLeave."),
        };

    public static HolidayView ToView(Holiday holiday) => new(holiday.Date, holiday.Name, Kind(holiday.Kind));
}

/// <summary>One year's holidays. No year asked for means the current year in Jakarta.</summary>
public sealed class ListHolidaysHandler(IHolidayStore store, IClock clock)
{
    public async Task<IReadOnlyList<HolidayView>> HandleAsync(int? year, CancellationToken ct)
    {
        var jakartaYear = clock.UtcNow.ToOffset(TimeSpan.FromHours(7)).Year;
        var holidays = await store.ListAsync(Holiday.RequireYear(year ?? jakartaYear), ct).ConfigureAwait(false);
        return [.. holidays.Select(HolidayWire.ToView)];
    }
}

/// <summary>Adds a holiday. A date that is already listed is refused (<c>holiday.exists</c>, 409), atomically with the write.</summary>
public sealed class AddHolidayHandler(
    ICurrentStaff staff,
    IHolidayStore store,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(AddHolidayRequest request, string? idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return idempotency.RunOnceAsync(
            $"holiday-add:{staff.StaffId}",
            idempotencyKey,
            Retention,
            async () =>
            {
                var holiday = Holiday.Create(request.Date, request.Name, HolidayWire.ParseKind(request.Kind));
                var audit = AuditEntry.Create(
                    clock, staff.StaffId, staff.StaffName, AuditActions.HolidayAdded, AuditTargets.Holiday, holiday.Date, staff.RequestId,
                    after: $"{holiday.Name} ({HolidayWire.Kind(holiday.Kind)})");

                return await store.AddAsync(holiday, audit, ct).ConfigureAwait(false) == HolidayWrite.Written
                    ? new ChangeDone()
                    : throw DomainException.For("holiday.exists", "That date is already in the holiday list.");
            },
            ct,
            InFlightPolicy.Reject);
    }
}

/// <summary>Removes a holiday. A date that is not listed answers 404 <c>holiday.not_found</c>.</summary>
public sealed class RemoveHolidayHandler(
    ICurrentStaff staff,
    IHolidayStore store,
    IIdempotencyStore idempotency,
    IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    public Task<ChangeDone> HandleAsync(string date, string? idempotencyKey, CancellationToken ct) =>
        idempotency.RunOnceAsync(
            $"holiday-remove:{staff.StaffId}:{date}",
            idempotencyKey,
            Retention,
            async () =>
            {
                // The date is caller input: only exactly yyyy-MM-dd reaches a key. Anything else is simply not a listed holiday.
                if (!Holiday.TryParseDate(date, out _))
                {
                    throw NotFound();
                }

                var existing = await store.FindAsync(date, ct).ConfigureAwait(false) ?? throw NotFound();
                var audit = AuditEntry.Create(
                    clock, staff.StaffId, staff.StaffName, AuditActions.HolidayRemoved, AuditTargets.Holiday, date, staff.RequestId,
                    before: $"{existing.Name} ({HolidayWire.Kind(existing.Kind)})");

                return await store.RemoveAsync(date, audit, ct).ConfigureAwait(false) == HolidayRemoval.Removed
                    ? new ChangeDone()
                    : throw NotFound();
            },
            ct,
            InFlightPolicy.Reject);

    private static DomainException NotFound() => DomainException.For("holiday.not_found", "That date is not in the holiday list.");
}
