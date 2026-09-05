using Koleksi.Domain.Common;
using Shouldly;

namespace Koleksi.Domain.Tests;

public sealed class MoneyTests
{
    [Theory]
    [InlineData(0, "Rp0")]
    [InlineData(1_250_000, "Rp1.250.000")]
    [InlineData(12_500_000, "Rp12.500.000")]
    [InlineData(420_000, "Rp420.000")]
    public void Formats_with_Indonesian_separators(long rupiah, string expected) =>
        Money.FromRupiah(rupiah).ToString().ShouldBe(expected);

    [Fact]
    public void Formatting_ignores_ambient_culture()
    {
        // The API runs with InvariantGlobalization, so a Money that read the ambient
        // culture would render "Rp1,250,000" to an Indonesian buyer.
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            Money.FromRupiah(1_250_000).ToString().ShouldBe("Rp1.250.000");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Fact]
    public void Rejects_negative_amounts() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Money.FromRupiah(-1));

    [Fact]
    public void Subtraction_below_zero_throws_rather_than_wrapping() =>
        Should.Throw<InvalidOperationException>(() => Money.FromRupiah(1000) - Money.FromRupiah(2000));

    [Fact]
    public void Addition_overflow_throws_rather_than_silently_wrapping() =>
        Should.Throw<OverflowException>(() => Money.FromRupiah(long.MaxValue) + Money.FromRupiah(1));

    [Fact]
    public void Percent_rounds_half_away_from_zero() =>
        // 5% of 1.000.001 is 50.000,05 which must land on 50.000, not drift by fractions.
        Money.FromRupiah(1_000_001).Percent(5m).ShouldBe(Money.FromRupiah(50_000));

    [Fact]
    public void AsPercentageOf_drives_the_offer_hint() =>
        Money.FromRupiah(850_000).AsPercentageOf(Money.FromRupiah(1_000_000)).ShouldBe(85m);

    [Fact]
    public void AsPercentageOf_zero_reference_does_not_divide_by_zero() =>
        Money.FromRupiah(100).AsPercentageOf(Money.Zero).ShouldBe(0m);
}
