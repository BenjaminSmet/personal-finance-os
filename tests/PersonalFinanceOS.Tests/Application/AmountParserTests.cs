using PersonalFinanceOS.Application.Parsing;

namespace PersonalFinanceOS.Tests.Application;

public class AmountParserTests
{
    [Theory]
    [InlineData("42,80", 4280)]
    [InlineData("42.80", 4280)]
    [InlineData("65", 6500)]
    [InlineData("17,99", 1799)]
    [InlineData("3,2", 320)]
    [InlineData("1.234,56", 123456)]
    [InlineData("1,234.56", 123456)]
    [InlineData("1 234,56", 123456)]
    [InlineData("€ 3,20", 320)]
    [InlineData("-42,80", -4280)]
    [InlineData("+10", 1000)]
    [InlineData("1.234.567", 123456700)]
    [InlineData(",50", 50)]
    public void Parses_common_formats(string input, long cents)
    {
        var result = AmountParser.Parse(input);
        Assert.True(result.Success, input);
        Assert.Equal(cents, result.Value.Cents);
        Assert.False(result.IsAmbiguous);
    }

    [Theory]
    [InlineData("1.234")]
    [InlineData("1,234")]
    public void Three_digits_after_one_separator_is_thousands_but_flagged(string input)
    {
        var result = AmountParser.Parse(input);
        Assert.True(result.Success);
        Assert.Equal(123400, result.Value.Cents);
        Assert.True(result.IsAmbiguous);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,345,67")]
    [InlineData("1,2345")]
    [InlineData("12.34.5")]
    [InlineData("1.23,45,6")]
    [InlineData("99999999999999999999")]
    public void Rejects_invalid_input(string input) =>
        Assert.False(AmountParser.Parse(input).Success, input);
}
