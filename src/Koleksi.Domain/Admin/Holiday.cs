using System.Globalization;
using Koleksi.Domain.Common;

namespace Koleksi.Domain.Admin;

public enum HolidayKind
{
    /// <summary>A national holiday (hari libur nasional).</summary>
    National = 0,

    /// <summary>A collective leave day (cuti bersama).</summary>
    CollectiveLeave = 1,
}

/// <summary>
/// A day the business-day count skips. Kept as data, edited by staff, every change audited.
/// </summary>
/// <param name="Date">A calendar date in Asia/Jakarta, <c>yyyy-MM-dd</c>.</param>
public sealed record Holiday(string Date, string Name, HolidayKind Kind)
{
    public const int MaxNameLength = 120;

    /// <summary>The earliest and latest year accepted, so a typo cannot create a partition for the year 0 or 9999.</summary>
    public const int MinYear = 2020;

    public const int MaxYear = 2100;

    /// <summary>Builds a holiday, or throws <c>admin.holiday_invalid</c>.</summary>
    public static Holiday Create(string? date, string? name, HolidayKind kind)
    {
        var trimmedDate = (date ?? string.Empty).Trim();
        var trimmedName = (name ?? string.Empty).Trim();

        if (!TryParseDate(trimmedDate, out _)
            || trimmedName.Length is 0 or > MaxNameLength
            || trimmedName.Any(char.IsControl)
            || !Enum.IsDefined(kind))
        {
            throw DomainException.For(
                "admin.holiday_invalid",
                $"A holiday needs a real date from {MinYear} to {MaxYear} and a name of up to {MaxNameLength} characters.");
        }

        return new Holiday(trimmedDate, trimmedName, kind);
    }

    /// <summary>The year of a date that is already known to be valid.</summary>
    public int Year => int.Parse(Date.AsSpan(0, 4), CultureInfo.InvariantCulture);

    /// <summary>True for exactly <c>yyyy-MM-dd</c> naming a real day in the accepted years.</summary>
    public static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        return value is { Length: 10 }
            && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && date.Year is >= MinYear and <= MaxYear;
    }

    /// <summary>The year a request asks for, or throws <c>admin.holiday_invalid</c>.</summary>
    public static int RequireYear(int year) =>
        year is >= MinYear and <= MaxYear
            ? year
            : throw DomainException.For("admin.holiday_invalid", $"The year must be {MinYear} to {MaxYear}.");
}

/// <summary>
/// The dates seeded for each year the code knows: national holidays and collective leave days of the SKB 3 Menteri (Setneg).
/// </summary>
/// <remarks>
/// Checked against the official publication on 2026-10-19 (https://www.setneg.go.id/baca/index/inilah_skb_3_menteri_libur_nasional_dan_cuti_bersama_2026):
/// 17 national holiday days (21 and 22 March are two) and 8 collective leave days (20, 23 and 24 March are three).
/// News sites disagree with it on a few dates (Idulfitri and Nyepi among them), so nothing here comes from them. Staff edit the
/// list in the admin site; a new year has to be added there (or here) before its business days are counted correctly.
/// <para>
/// 2027 was added on 2026-10-20 from the SKB signed on 15 September 2026 (Menag No. 1205, Menaker No. 3, PANRB No. 2 of 2026), read from
/// https://setneg.go.id/baca/index/inilah_skb_3_menteri_libur_nasional_dan_cuti_bersama_2027 and checked against a second source
/// (hukumonline.com): 18 national days and 8 collective leave days, 26 in all, every date identical. Isra Mikraj is listed twice
/// (5 January for 1448 H and 26 December for 1449 H); that is two different Hijri years, not a mistake. The decree PDF itself was not opened.
/// </para>
/// </remarks>
public static class HolidaySeed
{
    public static IReadOnlyList<Holiday> Year2027 { get; } =
    [
        new("2027-01-01", "Tahun Baru 2027 Masehi", HolidayKind.National),
        new("2027-01-05", "Isra Mikraj Nabi Muhammad saw.", HolidayKind.National),
        new("2027-02-05", "Cuti bersama Tahun Baru Imlek 2578 Kongzili", HolidayKind.CollectiveLeave),
        new("2027-02-06", "Tahun Baru Imlek 2578 Kongzili", HolidayKind.National),
        new("2027-03-08", "Hari Suci Nyepi (Tahun Baru Saka 1949)", HolidayKind.National),
        new("2027-03-09", "Cuti bersama Idulfitri 1448 H", HolidayKind.CollectiveLeave),
        new("2027-03-10", "Idulfitri 1448 H", HolidayKind.National),
        new("2027-03-11", "Idulfitri 1448 H", HolidayKind.National),
        new("2027-03-12", "Cuti bersama Idulfitri 1448 H", HolidayKind.CollectiveLeave),
        new("2027-03-15", "Cuti bersama Idulfitri 1448 H", HolidayKind.CollectiveLeave),
        new("2027-03-25", "Cuti bersama Wafat Yesus Kristus", HolidayKind.CollectiveLeave),
        new("2027-03-26", "Wafat Yesus Kristus", HolidayKind.National),
        new("2027-03-28", "Kebangkitan Yesus Kristus (Paskah)", HolidayKind.National),
        new("2027-05-01", "Hari Buruh Internasional", HolidayKind.National),
        new("2027-05-06", "Kenaikan Yesus Kristus", HolidayKind.National),
        new("2027-05-17", "Iduladha 1448 H", HolidayKind.National),
        new("2027-05-18", "Cuti bersama Iduladha 1448 H", HolidayKind.CollectiveLeave),
        new("2027-05-19", "Cuti bersama Hari Raya Waisak 2571 BE", HolidayKind.CollectiveLeave),
        new("2027-05-20", "Hari Raya Waisak 2571 BE", HolidayKind.National),
        new("2027-06-01", "Hari Lahir Pancasila", HolidayKind.National),
        new("2027-06-06", "1 Muharam Tahun Baru Islam 1449 H", HolidayKind.National),
        new("2027-08-15", "Maulid Nabi Muhammad saw.", HolidayKind.National),
        new("2027-08-17", "Proklamasi Kemerdekaan", HolidayKind.National),
        new("2027-12-24", "Cuti bersama Kelahiran Yesus Kristus", HolidayKind.CollectiveLeave),
        new("2027-12-25", "Kelahiran Yesus Kristus", HolidayKind.National),
        new("2027-12-26", "Isra Mikraj Nabi Muhammad saw.", HolidayKind.National),
    ];

    public static IReadOnlyList<Holiday> Year2026 { get; } =
    [
        new("2026-01-01", "Tahun Baru 2026 Masehi", HolidayKind.National),
        new("2026-01-16", "Isra Mikraj Nabi Muhammad saw.", HolidayKind.National),
        new("2026-02-16", "Cuti bersama Tahun Baru Imlek 2577 Kongzili", HolidayKind.CollectiveLeave),
        new("2026-02-17", "Tahun Baru Imlek 2577 Kongzili", HolidayKind.National),
        new("2026-03-18", "Cuti bersama Hari Suci Nyepi (Tahun Baru Saka 1948)", HolidayKind.CollectiveLeave),
        new("2026-03-19", "Hari Suci Nyepi (Tahun Baru Saka 1948)", HolidayKind.National),
        new("2026-03-20", "Cuti bersama Idulfitri 1447 H", HolidayKind.CollectiveLeave),
        new("2026-03-21", "Idulfitri 1447 H", HolidayKind.National),
        new("2026-03-22", "Idulfitri 1447 H", HolidayKind.National),
        new("2026-03-23", "Cuti bersama Idulfitri 1447 H", HolidayKind.CollectiveLeave),
        new("2026-03-24", "Cuti bersama Idulfitri 1447 H", HolidayKind.CollectiveLeave),
        new("2026-04-03", "Wafat Yesus Kristus", HolidayKind.National),
        new("2026-04-05", "Kebangkitan Yesus Kristus (Paskah)", HolidayKind.National),
        new("2026-05-01", "Hari Buruh Internasional", HolidayKind.National),
        new("2026-05-14", "Kenaikan Yesus Kristus", HolidayKind.National),
        new("2026-05-15", "Cuti bersama Kenaikan Yesus Kristus", HolidayKind.CollectiveLeave),
        new("2026-05-27", "Iduladha 1447 H", HolidayKind.National),
        new("2026-05-28", "Cuti bersama Iduladha 1447 H", HolidayKind.CollectiveLeave),
        new("2026-05-31", "Hari Raya Waisak 2570 BE", HolidayKind.National),
        new("2026-06-01", "Hari Lahir Pancasila", HolidayKind.National),
        new("2026-06-16", "1 Muharam Tahun Baru Islam 1448 H", HolidayKind.National),
        new("2026-08-17", "Proklamasi Kemerdekaan", HolidayKind.National),
        new("2026-08-25", "Maulid Nabi Muhammad saw.", HolidayKind.National),
        new("2026-12-24", "Cuti bersama Kelahiran Yesus Kristus", HolidayKind.CollectiveLeave),
        new("2026-12-25", "Kelahiran Yesus Kristus", HolidayKind.National),
    ];

    /// <summary>Every year the code can seed, by year. Each is seeded once, on its own marker, by the admin API's first start after it was added.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<Holiday>> ByYear { get; } = new Dictionary<int, IReadOnlyList<Holiday>>
    {
        [2026] = Year2026,
        [2027] = Year2027,
    };
}
