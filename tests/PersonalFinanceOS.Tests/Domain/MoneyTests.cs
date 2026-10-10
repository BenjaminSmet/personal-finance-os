using System.Globalization;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Tests.Domain;

public class MoneyTests
{
    [Theory]
    [InlineData("42.80", 4280)]
    [InlineData("0.01", 1)]
    [InlineData("-17.99", -1799)]
    [InlineData("2400", 240000)]
    public void FromEuros_converts_exact_amounts_to_cents(string euros, long cents) =>
        Assert.Equal(cents, Money.FromEuros(decimal.Parse(euros, CultureInfo.InvariantCulture)).Cents);

    [Fact]
    public void FromEuros_refuses_sub_cent_precision() =>
        Assert.Throws<ArgumentException>(() => Money.FromEuros(1.005m));

    [Theory]
    [InlineData("1.005", 101)]
    [InlineData("1.004", 100)]
    [InlineData("-1.005", -101)]
    [InlineData("0.125", 13)]
    public void Rounding_is_half_away_from_zero(string euros, long cents) =>
        Assert.Equal(cents, Money.FromEurosRounded(decimal.Parse(euros, CultureInfo.InvariantCulture)).Cents);

    [Fact]
    public void Arithmetic_is_exact()
    {
        // The classic floating-point trap: 0.10 + 0.20 must be exactly 0.30.
        var sum = Money.FromEuros(0.10m) + Money.FromEuros(0.20m);
        Assert.Equal(Money.FromEuros(0.30m), sum);
        Assert.Equal(Money.FromCents(-5), Money.FromCents(295) - Money.FromCents(300));
    }

    [Fact]
    public void Sum_adds_all_amounts() =>
        Assert.Equal(Money.FromCents(600), Money.Sum([Money.FromCents(100), Money.FromCents(200), Money.FromCents(300)]));

    [Fact]
    public void Overflow_throws_instead_of_wrapping() =>
        Assert.Throws<OverflowException>(() => Money.FromCents(long.MaxValue) + Money.FromCents(1));

    [Fact]
    public void Format_uses_dutch_conventions()
    {
        var text = Money.FromCents(123456).Format(CultureInfo.GetCultureInfo("nl-BE"));
        Assert.Contains("1.234,56", text, StringComparison.Ordinal);
        Assert.Contains("€", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_uses_english_conventions()
    {
        var text = Money.FromCents(123456).Format(CultureInfo.GetCultureInfo("en-IE"));
        Assert.Contains("1,234.56", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Comparison_operators_work()
    {
        Assert.True(Money.FromCents(1) > Money.Zero);
        Assert.True(Money.FromCents(-1).IsNegative);
        Assert.Equal(Money.FromCents(5), Money.FromCents(-5).Abs());
    }
}
