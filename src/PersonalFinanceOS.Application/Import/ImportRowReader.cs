using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.Application.Import;

/// <summary>Applies a mapping to every data line of a file (spec §8 step 5: validate rows, show errors).</summary>
public static class ImportRowReader
{
    public static IReadOnlyList<ParsedRow> Read(ImportFile file, ImportMapping mapping)
    {
        var result = new List<ParsedRow>();
        for (var i = mapping.FirstDataRow; i < file.Rows.Count; i++)
        {
            result.Add(ReadRow(file.Rows[i], i, mapping));
        }

        return result;
    }

    public static ParsedRow ReadRow(IReadOnlyList<string> row, int index, ImportMapping mapping)
    {
        string Cell(int? column) => column is { } c ? ImportDetector.Cell(row, c) : string.Empty;

        var description = Cell(mapping.DescriptionColumn);
        var counterparty = Cell(mapping.CounterpartyColumn);
        var reference = Cell(mapping.ReferenceColumn) is { Length: > 0 } r ? r : null;
        var date = ImportValues.ParseDate(Cell(mapping.DateColumn), mapping.DateFormat);
        var (amount, amountProblem) = ReadAmount(row, mapping);

        string? problem = null;
        if (date is null)
        {
            problem = "import.row.date";
        }
        else if (amountProblem is not null)
        {
            problem = amountProblem;
        }
        else if (Cell(mapping.CurrencyColumn) is { Length: > 0 } currency && !currency.Equals(Domain.Accounts.Account.SupportedCurrency, StringComparison.OrdinalIgnoreCase) && currency != "€")
        {
            // Spec §6: never treat a foreign-currency line as euros.
            problem = "import.row.currency";
        }

        return new ParsedRow(index, date, amount, description, counterparty, reference, problem);
    }

    private static (Money? Amount, string? Problem) ReadAmount(IReadOnlyList<string> row, ImportMapping mapping)
    {
        string Cell(int? column) => column is { } c ? ImportDetector.Cell(row, c) : string.Empty;
        var separator = mapping.DecimalSeparator;

        switch (mapping.Layout)
        {
            case AmountLayout.DebitCredit:
                var debitText = Cell(mapping.DebitColumn);
                var creditText = Cell(mapping.CreditColumn);
                var debit = ImportValues.ParseAmount(debitText, separator);
                var credit = ImportValues.ParseAmount(creditText, separator);
                if ((debitText.Length > 0 && debit is null) || (creditText.Length > 0 && credit is null))
                {
                    return (null, "import.row.amount");
                }

                // Money out may be written with or without a minus sign; it always leaves the account.
                var net = (credit?.Abs() ?? Money.Zero) - (debit?.Abs() ?? Money.Zero);
                return net.IsZero ? (null, "import.row.zero") : (net, null);

            case AmountLayout.WithDirection:
                var value = ImportValues.ParseAmount(Cell(mapping.AmountColumn), separator);
                if (value is null)
                {
                    return (null, "import.row.amount");
                }

                var outgoing = ImportValues.ParseDirection(Cell(mapping.DirectionColumn));
                if (outgoing is null)
                {
                    return (null, "import.row.direction");
                }

                var signed = outgoing.Value ? value.Value.Abs().Negate() : value.Value.Abs();
                return signed.IsZero ? (null, "import.row.zero") : (signed, null);

            default:
                var amount = ImportValues.ParseAmount(Cell(mapping.AmountColumn), separator);
                if (amount is null)
                {
                    return (null, "import.row.amount");
                }

                return amount.Value.IsZero ? (null, "import.row.zero") : (amount, null);
        }
    }
}
