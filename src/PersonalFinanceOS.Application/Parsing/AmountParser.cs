using System.Globalization;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Application.Parsing;

public sealed record AmountParseResult(bool Success, Money Value, bool IsAmbiguous)
{
    public static AmountParseResult Failed { get; } = new(false, Money.Zero, false);
}

/// <summary>
/// Parses amounts the way people type them in Belgium and in English:
/// "42,80", "42.80", "1.234,56", "1,234.56", "65", "-17,99", "€ 3,20".
/// "1.234" is read as 1234 (cents never have three digits) but flagged as ambiguous so the UI
/// can show a preview (spec §7). Works the same in Dutch and English. Never uses floating point.
/// </summary>
public static class AmountParser
{
    public static AmountParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return AmountParseResult.Failed;
        }

        var text = input.Replace("€", string.Empty, StringComparison.Ordinal)
            .Replace("EUR", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        var negative = false;
        if (text.StartsWith('-') || text.StartsWith('−'))
        {
            negative = true;
            text = text[1..];
        }
        else if (text.StartsWith('+'))
        {
            text = text[1..];
        }

        if (text.Length == 0 || !text.All(c => char.IsAsciiDigit(c) || c is ',' or '.'))
        {
            return AmountParseResult.Failed;
        }

        var ambiguous = false;
        char? decimalSeparator;
        var commas = text.Count(c => c == ',');
        var dots = text.Count(c => c == '.');

        if (commas > 0 && dots > 0)
        {
            // Whichever comes last is the decimal separator: "1.234,56" or "1,234.56".
            decimalSeparator = text.LastIndexOf(',') > text.LastIndexOf('.') ? ',' : '.';
            var thousands = decimalSeparator == ',' ? '.' : ',';
            if (text.Count(c => c == decimalSeparator) > 1 || text.IndexOf(thousands) > text.IndexOf(decimalSeparator.Value))
            {
                return AmountParseResult.Failed;
            }
        }
        else if (commas + dots == 0)
        {
            decimalSeparator = null;
        }
        else
        {
            var separator = commas > 0 ? ',' : '.';
            var count = Math.Max(commas, dots);
            var digitsAfter = text.Length - text.LastIndexOf(separator) - 1;

            if (count > 1)
            {
                decimalSeparator = null; // "1.234.567" → thousands only
            }
            else if (digitsAfter is 1 or 2)
            {
                decimalSeparator = separator;
            }
            else if (digitsAfter == 3)
            {
                // Money has at most two decimals, so "1.234" / "1,234" can only sensibly mean 1234.
                // Still flagged, because the user may have mistyped "1,23" or "12,34".
                ambiguous = true;
                decimalSeparator = null;
            }
            else
            {
                return AmountParseResult.Failed;
            }
        }

        string integerPart;
        string fractionPart;
        if (decimalSeparator is { } sep)
        {
            var index = text.LastIndexOf(sep);
            integerPart = text[..index];
            fractionPart = text[(index + 1)..];
        }
        else
        {
            integerPart = text;
            fractionPart = string.Empty;
        }

        if (!ValidThousandsGrouping(integerPart))
        {
            return AmountParseResult.Failed;
        }

        integerPart = integerPart.Replace(",", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal);
        if (integerPart.Length == 0)
        {
            integerPart = "0";
        }

        if (fractionPart.Length > 2)
        {
            // Three decimals are not cents. ("1.234" without a matching culture was handled above.)
            return AmountParseResult.Failed;
        }

        fractionPart = fractionPart.PadRight(2, '0');

        if (!long.TryParse(integerPart, NumberStyles.None, CultureInfo.InvariantCulture, out var euros) ||
            !long.TryParse(fractionPart, NumberStyles.None, CultureInfo.InvariantCulture, out var cents))
        {
            return AmountParseResult.Failed;
        }

        try
        {
            var total = checked((euros * 100) + cents);
            return new AmountParseResult(true, Money.FromCents(negative ? -total : total), ambiguous);
        }
        catch (OverflowException)
        {
            return AmountParseResult.Failed;
        }
    }

    /// <summary>Thousands separators must split the integer into groups of exactly three digits.</summary>
    private static bool ValidThousandsGrouping(string integerPart)
    {
        if (!integerPart.Contains(',') && !integerPart.Contains('.'))
        {
            return true;
        }

        var groups = integerPart.Split(',', '.');
        return groups[0].Length is >= 1 and <= 3 && groups.Skip(1).All(g => g.Length == 3);
    }
}
