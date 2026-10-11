using System.Text;
using PersonalFinanceOS.Application.Reports;

namespace PersonalFinanceOS.Infrastructure.Reports;

/// <summary>
/// Writes a <see cref="FormattedReport"/> as CSV (spec §18): the report heading, its notes, then each table with its
/// header row. Numbers are written without currency symbols or thousands separators, with the decimal separator of
/// the report's language, so a spreadsheet reads them as numbers. UTF-8 with a byte order mark, so Excel shows the
/// euro sign and accents.
/// </summary>
public static class CsvReportWriter
{
    public static byte[] Write(FormattedReport report)
    {
        var separator = report.CsvSeparator;
        var sb = new StringBuilder();
        void Line(IEnumerable<string> fields) => sb.Append(string.Join(separator, fields.Select(f => Field(f, separator)))).Append("\r\n");

        Line([report.Title]);
        Line([report.Period]);
        Line([report.Generated]);
        foreach (var note in report.Notes)
        {
            Line([note]);
        }

        foreach (var table in report.Tables)
        {
            sb.Append("\r\n");
            Line([table.Title]);
            Line(table.Columns.Select(c => c.Header));
            foreach (var row in table.Rows)
            {
                Line(row.Values);
            }

            if (table.Rows.Count == 0 && table.EmptyText is { } empty)
            {
                Line([empty]);
            }

            if (table.Note is { } tableNote)
            {
                Line([tableNote]);
            }
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// Quotes a field when needed. Text that a spreadsheet would run as a formula (a bank description starting with
    /// "=", "+", "-" or "@") gets a leading apostrophe; plain numbers, negative ones included, are left as they are.
    /// </summary>
    internal static string Field(string value, char separator)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]) && !IsNumber(value))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([separator, '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static bool IsNumber(string value) =>
        value.Length > 1 && value[0] == '-' && value.Skip(1).All(c => char.IsAsciiDigit(c) || c is ',' or '.');
}
