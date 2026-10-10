namespace PersonalFinanceOS.Application.Import;

/// <summary>
/// Guesses the mapping of an unknown bank file (spec §8 step 2): header line, date format, decimal
/// separator, debit/credit layout and the likely columns. Only a suggestion: the user sees a preview
/// and can change every choice before anything is imported.
/// </summary>
public static class ImportDetector
{
    private const int Sample = 60;

    // Header words per meaning, in Dutch, English and French, already normalised. Exact matches win
    // over "contains" matches; earlier entries win over later ones.
    private static readonly string[] DateWords = ["boekingsdatum", "datum", "date", "transactiedatum", "uitvoeringsdatum", "verrichtingsdatum", "booking date", "transaction date", "date operation", "date d execution"];
    private static readonly string[] ValueDateWords = ["valutadatum", "valuta datum", "value date", "date valeur", "rentedatum"];
    private static readonly string[] AmountWords = ["bedrag", "amount", "montant", "bedrag eur", "transactiebedrag", "bedrag in eur", "amount eur"];
    private static readonly string[] DebitWords = ["debet", "debit", "af", "uitgaven", "afschrijving", "money out", "paid out"];
    private static readonly string[] CreditWords = ["credit", "bij", "ontvangsten", "bijschrijving", "money in", "paid in"];
    private static readonly string[] DirectionWords = ["af bij", "debet credit", "credit debet", "d c", "debit credit", "credit debit", "richting"];
    private static readonly string[] CounterpartyWords = ["naam tegenpartij", "tegenpartij", "naam", "counterparty", "counterparty name", "payee", "begunstigde", "name", "nom contrepartie", "contrepartie", "naam begunstigde"];
    private static readonly string[] DescriptionWords = ["omschrijving", "mededeling", "description", "details", "communicatie", "vrije mededeling", "memo", "mededelingen", "communication", "libelle", "transaction details"];
    private static readonly string[] ReferenceWords = ["referentie", "reference", "transactienummer", "transactie id", "transaction id", "bankreferentie", "volgnummer", "volgnr", "ref", "id"];
    private static readonly string[] CurrencyWords = ["munt", "devies", "devise", "currency", "munteenheid", "valuta"];
    private static readonly string[] NotAmountWords = ["saldo", "balance", "solde", "nieuw saldo", "oud saldo"];

    public static ImportMapping Detect(ImportFile file)
    {
        var rows = file.Rows;
        var columns = file.ColumnCount;
        var sample = rows.Take(Sample).ToList();

        var (dateColumn, dateFormat) = BestDateColumn(sample, columns);
        var headerRow = HeaderRow(sample, dateColumn, dateFormat);
        var header = headerRow >= 0 ? rows[headerRow].Select(ImportText.Normalize).ToList() : [];
        var data = rows.Skip(headerRow + 1).Take(Sample).ToList();

        if (headerRow >= 0 && Find(header, DateWords, exclude: ValueDateWords) is { } namedDate && DateFormat(data, namedDate) is { } namedFormat)
        {
            (dateColumn, dateFormat) = (namedDate, namedFormat);
        }

        var used = new HashSet<int> { dateColumn };
        int? Pick(string[] words, string[]? exclude = null)
        {
            if (headerRow < 0)
            {
                return null;
            }

            var column = Find(header, words, used, exclude);
            if (column is { } c)
            {
                used.Add(c);
            }

            return column;
        }

        // Value dates, balances and the like are claimed first so they are never mistaken for the real thing.
        Pick(ValueDateWords);
        var balance = Pick(NotAmountWords);
        var currency = Pick(CurrencyWords);
        if (currency is { } cur && !data.Where(r => Cell(r, cur).Length > 0).All(r => IsCurrencyCode(Cell(r, cur))))
        {
            // "Valuta" is a value date in Belgian exports: only a column of codes like "EUR" is a currency.
            used.Remove(cur);
            currency = null;
        }
        var direction = Pick(DirectionWords);
        var amount = Pick(AmountWords);
        var debit = amount is null ? Pick(DebitWords) : null;
        var credit = amount is null && debit is not null ? Pick(CreditWords) : null;
        var reference = Pick(ReferenceWords);
        var counterparty = Pick(CounterpartyWords);
        var description = Pick(DescriptionWords);

        var layout = AmountLayout.Signed;
        if (amount is null && debit is not null && credit is not null)
        {
            layout = AmountLayout.DebitCredit;
        }
        else
        {
            amount ??= BestAmountColumn(data, columns, used, balance);
            if (amount is not null && direction is not null && !HasNegative(data, amount.Value))
            {
                layout = AmountLayout.WithDirection;
            }
            else
            {
                direction = null;
            }
        }

        if (amount is { } a)
        {
            used.Add(a);
        }

        if (description is null && counterparty is null)
        {
            description = LongestTextColumn(data, columns, used);
        }

        var amountColumns = layout == AmountLayout.DebitCredit ? new[] { debit!.Value, credit!.Value } : amount is { } ac ? [ac] : [];
        var decimalSeparator = DecimalSeparator(data, amountColumns, file.Format);

        return new ImportMapping(
            headerRow, dateColumn, dateFormat, layout,
            layout == AmountLayout.DebitCredit ? null : amount,
            layout == AmountLayout.DebitCredit ? debit : null,
            layout == AmountLayout.DebitCredit ? credit : null,
            layout == AmountLayout.WithDirection ? direction : null,
            decimalSeparator, description, counterparty, reference, currency);
    }

    /// <summary>
    /// The format that reads the most non-empty values of the column (earlier formats win ties, so
    /// day-first beats month-first), or null when none reads any. A few bad lines do not change it;
    /// they are reported as invalid later.
    /// </summary>
    public static string? DateFormat(IEnumerable<IReadOnlyList<string>> rows, int column)
    {
        var values = rows.Select(r => Cell(r, column)).Where(v => v.Length > 0).ToList();
        string? best = null;
        var bestCount = 0;
        foreach (var format in ImportMapping.DateFormats)
        {
            var count = values.Count(v => ImportValues.ParseDate(v, format) is not null);
            if (count > bestCount)
            {
                best = format;
                bestCount = count;
            }
        }

        return best;
    }

    /// <summary>"," or "." from how the amounts are written; ties fall back to the format's usual one.</summary>
    public static char DecimalSeparator(IEnumerable<IReadOnlyList<string>> rows, IReadOnlyList<int> columns, string format)
    {
        var comma = 0;
        var dot = 0;
        foreach (var value in rows.SelectMany(r => columns.Select(c => Cell(r, c))).Where(v => v.Length > 0))
        {
            var last = value.LastIndexOfAny([',', '.']);
            if (last < 0)
            {
                continue;
            }

            var digitsAfter = value[(last + 1)..].TakeWhile(char.IsAsciiDigit).Count();
            if (digitsAfter is 1 or 2)
            {
                if (value[last] == ',')
                {
                    comma++;
                }
                else
                {
                    dot++;
                }
            }
            else if (digitsAfter == 3)
            {
                // "1.234" or "1,234": a thousands separator, so the decimal one is the other.
                if (value[last] == ',')
                {
                    dot++;
                }
                else
                {
                    comma++;
                }
            }
        }

        if (comma == dot)
        {
            return format == "xlsx" ? '.' : ',';
        }

        return comma > dot ? ',' : '.';
    }

    private static (int Column, string Format) BestDateColumn(List<IReadOnlyList<string>> rows, int columns)
    {
        var best = (Column: 0, Format: ImportMapping.DateFormats[0], Score: -1);
        for (var c = 0; c < columns; c++)
        {
            foreach (var format in ImportMapping.DateFormats)
            {
                // Pure numbers like amounts or ids must not pass as yyyyMMdd dates.
                var score = rows.Count(r => Cell(r, c) is { Length: >= 6 } v && ImportValues.ParseDate(v, format) is not null && (format != "yyyyMMdd" || v.Length == 8));
                if (score > best.Score)
                {
                    best = (c, format, score);
                }
            }
        }

        // The format must read all data rows of that column, not just most: refine on the data rows.
        var data = rows.Where(r => ImportValues.ParseDate(Cell(r, best.Column), best.Format) is not null).ToList();
        return (best.Column, DateFormat(data, best.Column) ?? best.Format);
    }

    /// <summary>The line just above the first line with a date, when it looks like a header.</summary>
    private static int HeaderRow(List<IReadOnlyList<string>> rows, int dateColumn, string dateFormat)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ImportValues.ParseDate(Cell(rows[i], dateColumn), dateFormat) is not null && rows[i].Count(c => c.Length > 0) >= 2)
            {
                return i - 1;
            }
        }

        return -1;
    }

    private static int? Find(List<string> header, string[] words, HashSet<int>? used = null, string[]? exclude = null)
    {
        bool Free(int i) => (used is null || !used.Contains(i)) && (exclude is null || !exclude.Contains(header[i]));

        foreach (var word in words)
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (Free(i) && header[i] == word)
                {
                    return i;
                }
            }
        }

        foreach (var word in words.Where(w => w.Length >= 4))
        {
            for (var i = 0; i < header.Count; i++)
            {
                if (Free(i) && ImportText.ContainsWords(header[i], word))
                {
                    return i;
                }
            }
        }

        return null;
    }

    private static int? BestAmountColumn(List<IReadOnlyList<string>> rows, int columns, HashSet<int> used, int? balance)
    {
        int? best = null;
        var bestScore = 0;
        for (var c = 0; c < columns; c++)
        {
            if (used.Contains(c) || c == balance)
            {
                continue;
            }

            var values = rows.Select(r => Cell(r, c)).Where(v => v.Length > 0).ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var amounts = values.Count(v => (v.Contains(',') || v.Contains('.') || v.StartsWith('-')) &&
                                           (ImportValues.ParseAmount(v, ',') is not null || ImportValues.ParseAmount(v, '.') is not null));
            var negatives = values.Count(v => v.TrimStart().StartsWith('-'));
            var score = (amounts * 2) + negatives;
            if (amounts * 2 >= values.Count && score > bestScore)
            {
                best = c;
                bestScore = score;
            }
        }

        return best;
    }

    private static bool HasNegative(List<IReadOnlyList<string>> rows, int column) =>
        rows.Any(r => Cell(r, column).TrimStart().StartsWith('-'));

    private static int? LongestTextColumn(List<IReadOnlyList<string>> rows, int columns, HashSet<int> used)
    {
        int? best = null;
        var bestLength = 0.0;
        for (var c = 0; c < columns; c++)
        {
            if (used.Contains(c))
            {
                continue;
            }

            var values = rows.Select(r => Cell(r, c)).ToList();
            var letters = values.Average(v => v.Count(char.IsLetter));
            if (letters > bestLength)
            {
                best = c;
                bestLength = letters;
            }
        }

        return best;
    }

    private static bool IsCurrencyCode(string value) => value == "€" || (value.Length == 3 && value.All(char.IsAsciiLetter));

    internal static string Cell(IReadOnlyList<string> row, int column) => column >= 0 && column < row.Count ? row[column] : string.Empty;
}
