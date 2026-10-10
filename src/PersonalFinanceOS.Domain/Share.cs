using System.Globalization;

namespace PersonalFinanceOS.Domain;

/// <summary>
/// An ownership share between 0% and 100%, stored in basis points (1/100 of a percent)
/// so it never needs floating point. 50% = 5000 bp. Never hard-coded (spec §9).
/// </summary>
public readonly record struct Share
{
    public const int FullBasisPoints = 10_000;

    public int BasisPoints { get; }

    public Share(int basisPoints)
    {
        if (basisPoints is < 0 or > FullBasisPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(basisPoints), "A share must be between 0% and 100%.");
        }

        BasisPoints = basisPoints;
    }

    public static Share Full => new(FullBasisPoints);
    public static Share None => new(0);

    /// <summary>Creates a share from a percentage such as 50 or 33.33 (max two decimals).</summary>
    public static Share FromPercent(decimal percent)
    {
        var bp = percent * 100m;
        if (bp != decimal.Truncate(bp))
        {
            throw new ArgumentException("A share supports at most two decimals.", nameof(percent));
        }

        if (bp is < 0 or > FullBasisPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(percent), "A share must be between 0% and 100%.");
        }

        return new Share((int)bp);
    }

    public decimal Percent => BasisPoints / 100m;

    public bool IsFull => BasisPoints == FullBasisPoints;

    /// <summary>The user's part of an amount, rounded with the app rounding policy.</summary>
    public Money ApplyTo(Money amount)
    {
        if (IsFull)
        {
            return amount;
        }

        var cents = (decimal)amount.Cents * BasisPoints / FullBasisPoints;
        return Money.FromCents(checked((long)RoundingPolicy.RoundToCents(cents)));
    }

    public string Format(CultureInfo culture) => Percent.ToString("0.##", culture) + " %";

    public override string ToString() => Format(CultureInfo.InvariantCulture);
}
