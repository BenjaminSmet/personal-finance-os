using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Tests.Domain;

public class ShareTests
{
    [Theory]
    [InlineData(5000, 100000, 50000)]
    [InlineData(5000, 1001, 501)]   // 500.5 cents rounds away from zero
    [InlineData(5000, -1001, -501)]
    [InlineData(3333, 10000, 3333)]
    [InlineData(10000, 12345, 12345)]
    public void ApplyTo_takes_the_users_part(int basisPoints, long cents, long expected) =>
        Assert.Equal(expected, new Share(basisPoints).ApplyTo(Money.FromCents(cents)).Cents);

    [Fact]
    public void FromPercent_supports_two_decimals()
    {
        Assert.Equal(3333, Share.FromPercent(33.33m).BasisPoints);
        Assert.Throws<ArgumentException>(() => Share.FromPercent(33.333m));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void Out_of_range_is_rejected(int basisPoints) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Share(basisPoints));
}
