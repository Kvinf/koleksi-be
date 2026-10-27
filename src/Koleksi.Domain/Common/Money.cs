namespace Koleksi.Domain.Common;

/// <summary>
/// An amount of Indonesian Rupiah, held as a whole number of rupiah.
/// </summary>
/// <remarks>
/// <para>
/// Rupiah is not subdivided in practice — the sen was withdrawn from circulation
/// decades ago and no Indonesian payment rail settles fractions. Money is therefore
/// stored as a <see cref="long"/> count of whole rupiah rather than a decimal.
/// </para>
/// <para>
/// Never use <c>double</c>/<c>float</c> for money anywhere in this codebase. A
/// <see cref="long"/> represents every attainable listing price exactly
/// (<c>long.MaxValue</c> is roughly nine quintillion rupiah), and arithmetic is
/// checked so an overflow throws instead of silently wrapping into a negative price.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    public static readonly Money Zero = new(0);

    private Money(long rupiah) => Rupiah = rupiah;

    /// <summary>The amount, as a whole number of rupiah. Never negative.</summary>
    public long Rupiah { get; }

    /// <summary>Creates an amount, rejecting negatives.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    public static Money FromRupiah(long rupiah) => rupiah < 0
        ? throw new ArgumentOutOfRangeException(nameof(rupiah), rupiah, "Money cannot be negative.")
        : new Money(rupiah);

    /// <summary>Creates an amount, or returns <c>null</c> when the input is not a valid amount.</summary>
    public static Money? TryFromRupiah(long rupiah) => rupiah < 0 ? null : new Money(rupiah);

    public bool IsZero => Rupiah == 0;

    public static Money operator +(Money left, Money right) => new(checked(left.Rupiah + right.Rupiah));

    /// <exception cref="InvalidOperationException">The result would be negative.</exception>
    public static Money operator -(Money left, Money right) => left.Rupiah < right.Rupiah
        ? throw new InvalidOperationException($"Subtracting {right} from {left} would produce a negative amount.")
        : new Money(left.Rupiah - right.Rupiah);

    public static Money operator *(Money amount, int quantity) => quantity < 0
        ? throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity cannot be negative.")
        : new Money(checked(amount.Rupiah * quantity));

    public static bool operator <(Money left, Money right) => left.Rupiah < right.Rupiah;
    public static bool operator >(Money left, Money right) => left.Rupiah > right.Rupiah;
    public static bool operator <=(Money left, Money right) => left.Rupiah <= right.Rupiah;
    public static bool operator >=(Money left, Money right) => left.Rupiah >= right.Rupiah;

    /// <summary>
    /// Multiplies by a rate (a fee percentage, an offer ratio) and rounds half away
    /// from zero, the convention Indonesian invoices use.
    /// </summary>
    public Money Percent(decimal percentage) => percentage < 0
        ? throw new ArgumentOutOfRangeException(nameof(percentage), percentage, "Percentage cannot be negative.")
        : new Money((long)Math.Round(Rupiah * percentage / 100m, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Whether this amount is strictly less than <paramref name="percentage"/> percent of
    /// <paramref name="reference"/>, compared exactly: no rounding, so 99.999 of 200.000 (49.9995%) is
    /// below 50%. Decimal holds any rupiah amount times 100 without overflow.
    /// </summary>
    public bool IsBelowPercentOf(Money reference, decimal percentage) =>
        (decimal)Rupiah * 100m < (decimal)reference.Rupiah * percentage;

    /// <summary>This amount expressed as a percentage of <paramref name="reference"/>.</summary>
    /// <remarks>Used for the "X% of asking" hint on the offer modal.</remarks>
    public decimal AsPercentageOf(Money reference) => reference.IsZero
        ? 0m
        : Math.Round(Rupiah * 100m / reference.Rupiah, 2, MidpointRounding.AwayFromZero);

    public int CompareTo(Money other) => Rupiah.CompareTo(other.Rupiah);

    /// <summary>
    /// Renders the amount the way the design specifies — <c>Rp1.250.000</c>.
    /// </summary>
    /// <remarks>
    /// The culture is pinned to <c>id-ID</c> explicitly. The API runs with
    /// <c>InvariantGlobalization</c>, so relying on an ambient culture would produce
    /// <c>Rp1,250,000</c> instead, with the wrong separators for the audience.
    /// </remarks>
    public override string ToString() => "Rp" + Rupiah.ToString("#,##0", IndonesianNumberFormat);

    private static readonly System.Globalization.NumberFormatInfo IndonesianNumberFormat = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = [3],
    };
}
