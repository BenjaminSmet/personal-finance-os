using System.Globalization;

namespace PersonalFinanceOS.Domain;

/// <summary>
/// An amount of euros stored as an exact number of cents.
/// Binary floating point is never used for money (spec §6).
/// v1.0 is single-currency (EUR); the currency lives on the account, not on every amount.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    public long Cents { get; }

    public Money(long cents) => Cents = cents;

    public static Money Zero => default;

    public static Money FromCents(long cents) => new(cents);

    /// <summary>
    /// Converts an exact decimal euro amount. Throws when the value has more than two decimals,
    /// so sub-cent precision is never silently dropped. Use <see cref="FromEurosRounded"/> when
    /// rounding is intended (e.g. results of percentage calculations).
    /// </summary>
    public static Money FromEuros(decimal euros)
    {
        var scaled = euros * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new ArgumentException($"Amount {euros.ToString(CultureInfo.InvariantCulture)} has more than two decimals.", nameof(euros));
        }

        return new Money(checked((long)scaled));
    }

    /// <summary>Converts a decimal euro amount using the app-wide rounding policy.</summary>
    public static Money FromEurosRounded(decimal euros) =>
        new(checked((long)RoundingPolicy.RoundToCents(euros * 100m)));

    public decimal ToEuros() => Cents / 100m;

    public bool IsZero => Cents == 0;
    public bool IsNegative => Cents < 0;
    public bool IsPositive => Cents > 0;

    public Money Abs() => new(Math.Abs(Cents));
    public Money Negate() => new(checked(-Cents));

    public static Money operator +(Money a, Money b) => new(checked(a.Cents + b.Cents));
    public static Money operator -(Money a, Money b) => new(checked(a.Cents - b.Cents));
    public static Money operator -(Money a) => a.Negate();
    public static bool operator <(Money a, Money b) => a.Cents < b.Cents;
    public static bool operator >(Money a, Money b) => a.Cents > b.Cents;
    public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;
    public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;

    public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

    public static Money Sum(IEnumerable<Money> amounts)
    {
        long total = 0;
        foreach (var amount in amounts)
        {
            total = checked(total + amount.Cents);
        }

        return new Money(total);
    }

    /// <summary>Culture-aware currency text, e.g. "€ 1.234,56" (nl-BE) or "€1,234.56" (en-IE).</summary>
    public string Format(CultureInfo culture)
    {
        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencySymbol = "€";
        return ToEuros().ToString("C2", format);
    }

    public override string ToString() => Format(CultureInfo.InvariantCulture);
}

/// <summary>
/// The single rounding policy for the whole app (spec §6: deterministic rounding).
/// Half away from zero matches how Belgian banks and most people round euro amounts.
/// </summary>
public static class RoundingPolicy
{
    public const MidpointRounding Mode = MidpointRounding.AwayFromZero;

    public static decimal RoundToCents(decimal cents) => decimal.Round(cents, 0, Mode);
}
