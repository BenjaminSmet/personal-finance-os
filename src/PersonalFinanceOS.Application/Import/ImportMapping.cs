using System.Globalization;
using System.Text;
using System.Text.Json;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Application.Import;

/// <summary>How a bank file gives the direction of money.</summary>
public enum AmountLayout
{
    /// <summary>One amount column, negative for money out.</summary>
    Signed,

    /// <summary>Separate columns for money out and money in.</summary>
    DebitCredit,

    /// <summary>One positive amount column plus a column saying "Af"/"Bij", "D"/"C"…</summary>
    WithDirection,
}

/// <summary>
/// Which column means what (spec §8 step 3). Column numbers are 0-based. <see cref="HeaderRow"/> is the
/// index of the header line (-1 when the file has none); everything above it is ignored, everything
/// below it is data.
/// </summary>
public sealed record ImportMapping(
    int HeaderRow,
    int DateColumn,
    string DateFormat,
    AmountLayout Layout,
    int? AmountColumn,
    int? DebitColumn,
    int? CreditColumn,
    int? DirectionColumn,
    char DecimalSeparator,
    int? DescriptionColumn,
    int? CounterpartyColumn,
    int? ReferenceColumn,
    int? CurrencyColumn)
{
    /// <summary>Date patterns offered and recognised, day-first before month-first (Belgian exports).</summary>
    public static IReadOnlyList<string> DateFormats { get; } =
    [
        "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "yyyy-M-d", "yyyy/M/d", "yyyyMMdd", "d/M/yy", "d-M-yy", "d.M.yy", "M/d/yyyy",
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public int FirstDataRow => HeaderRow + 1;

    /// <summary>The columns this mapping uses, to check it against a file.</summary>
    public IEnumerable<int> UsedColumns =>
        new int?[] { DateColumn, AmountColumn, DebitColumn, CreditColumn, DirectionColumn, DescriptionColumn, CounterpartyColumn, ReferenceColumn, CurrencyColumn }
            .Where(c => c is not null).Select(c => c!.Value);

    /// <summary>Null when complete; otherwise the code of what is missing.</summary>
    public string? Problem(int columnCount)
    {
        if (UsedColumns.Any(c => c < 0 || c >= columnCount))
        {
            return "import.mapping.column";
        }

        if (!DateFormats.Contains(DateFormat))
        {
            return "import.mapping.date";
        }

        return Layout switch
        {
            AmountLayout.Signed when AmountColumn is null => "import.mapping.amount",
            AmountLayout.DebitCredit when DebitColumn is null || CreditColumn is null => "import.mapping.amount",
            AmountLayout.WithDirection when AmountColumn is null || DirectionColumn is null => "import.mapping.amount",
            _ => DescriptionColumn is null && CounterpartyColumn is null ? "import.mapping.description" : null,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ImportMapping? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ImportMapping>(json, Json);
        }
        catch (JsonException)
        {
            return null; // a damaged remembered mapping is simply detected again
        }
    }

    /// <summary>Normalised header line used to recognise a known bank layout, e.g. "datum|bedrag|omschrijving".</summary>
    public static string? HeaderKey(ImportFile file, int headerRow)
    {
        if (headerRow < 0 || headerRow >= file.Rows.Count)
        {
            return null;
        }

        return file.Format + ":" + string.Join('|', file.Rows[headerRow].Select(ImportText.Normalize));
    }
}

/// <summary>One data line of a bank file after applying a mapping.</summary>
public sealed record ParsedRow(
    int RowIndex,
    DateOnly? Date,
    Money? Amount,
    string Description,
    string Counterparty,
    string? Reference,
    string? Problem)
{
    /// <summary>1-based line number as the user sees it in the preview.</summary>
    public int RowNumber => RowIndex + 1;
}

/// <summary>Turns text cells into dates and amounts. Deterministic and culture independent.</summary>
public static class ImportValues
{
    private static readonly string[] Debit = ["af", "d", "dt", "debet", "debit", "-", "uitgaand", "uitgave", "out"];
    private static readonly string[] Credit = ["bij", "c", "cr", "credit", "+", "inkomend", "ontvangst", "in"];

    public static DateOnly? ParseDate(string? text, string format)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // "2026-10-05 00:00:00" or "05/10/2026 14:12": the time is not needed.
        var value = text.Trim();
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            value = value[..space];
        }

        value = value.Replace('T', ' ').Split(' ')[0];

        // "yyyy" must really be four digits and "yy" two, or "05/10/26" would become the year 26.
        var longestDigitRun = value.Split(['/', '-', '.']).Max(p => p.Length);
        var fourDigitYear = format.Contains("yyyy", StringComparison.Ordinal);
        if (format != "yyyyMMdd" && (fourDigitYear ? longestDigitRun != 4 : longestDigitRun > 2))
        {
            return null;
        }

        return DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>
    /// "-1.234,56", "1,234.56", "€ 42,80", "42,80-", "(42,80)", "+5" with a known decimal separator.
    /// Never floating point; more than two decimals is not money and fails.
    /// </summary>
    public static Money? ParseAmount(string? text, char decimalSeparator)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder();
        var negative = false;
        var value = text.Trim();
        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            negative = true;
            value = value[1..^1];
        }

        foreach (var c in value)
        {
            switch (c)
            {
                case '-' or '−':
                    negative = !negative;
                    break;
                case '+' or ' ' or ' ' or ' ' or '\'' or '€':
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        var cleaned = builder.ToString();
        if (cleaned.EndsWith("EUR", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[..^3];
        }

        var thousands = decimalSeparator == ',' ? '.' : ',';
        cleaned = cleaned.Replace(thousands.ToString(), string.Empty, StringComparison.Ordinal);
        var parts = cleaned.Split(decimalSeparator);
        if (parts.Length > 2 || (parts[0].Length == 0 && (parts.Length == 1 || parts[1].Length == 0)))
        {
            return null;
        }

        var whole = parts[0].Length == 0 ? "0" : parts[0];
        var fraction = parts.Length == 2 ? parts[1] : string.Empty;
        if (fraction.Length > 2 || !whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit) || whole.Length > 13)
        {
            return null;
        }

        var cents = (long.Parse(whole, CultureInfo.InvariantCulture) * 100) + long.Parse(fraction.PadRight(2, '0'), CultureInfo.InvariantCulture);
        return Money.FromCents(negative ? -cents : cents);
    }

    /// <summary>True for money out, false for money in, null when the marker is not recognised.</summary>
    public static bool? ParseDirection(string? text)
    {
        var value = ImportText.Normalize(text);
        if (Debit.Contains(value))
        {
            return true;
        }

        return Credit.Contains(value) ? false : null;
    }
}

/// <summary>Text comparisons for headers, payees and duplicates.</summary>
public static class ImportText
{
    /// <summary>Lower case, accents removed, anything that is not a letter or digit becomes one space.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c) || c is '+' or '-')
            {
                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else
            {
                space = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>Whole-word containment: "spotify" is in "spotify ab stockholm", "ing" is not in "parking".</summary>
    public static bool ContainsWords(string normalizedText, string normalizedWords)
    {
        if (normalizedWords.Length == 0)
        {
            return false;
        }

        var padded = " " + normalizedText + " ";
        return padded.Contains(" " + normalizedWords + " ", StringComparison.Ordinal);
    }
}
