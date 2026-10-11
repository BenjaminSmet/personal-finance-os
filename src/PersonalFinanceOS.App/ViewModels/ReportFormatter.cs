using System.Globalization;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Domain;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>
/// Turns a <see cref="Report"/> into text in the current language. The screen, the PDF and the CSV all show this one
/// result, so the values on screen and in the exports are always the same (spec §23).
/// </summary>
public static class ReportFormatter
{
    public static FormattedReport Format(Report report)
    {
        var culture = Loc.Instance.Culture;
        var title = Text(report.Title);
        return new FormattedReport(
            title,
            Loc.Format("Report_PeriodLabel", LongDate(report.From), LongDate(report.To)),
            Loc.Format("Report_Generated", LongDate(DateOnly.FromDateTime(report.GeneratedAt.DateTime)), report.GeneratedAt.ToString("HH:mm", culture)),
            report.Notes.Select(Text).ToList(),
            report.Tables.Select(Table).ToList(),
            FileName(title, report.From, report.To),
            Loc.T("Report_Page"),
            culture.NumberFormat.NumberDecimalSeparator == "," ? ';' : ',');
    }

    public static string Text(ReportText text) =>
        text.Args.Length == 0 ? Loc.T(text.Key) : Loc.Format(text.Key, text.Args.Select(a => Cell(a, inText: true)).ToArray());

    private static FormattedTable Table(ReportTable table) => new(
        Text(table.Title),
        table.Columns.Select(c => new FormattedColumn(Text(c.Header), c.Align)).ToList(),
        table.Rows.Select(r => new FormattedRow(r.Cells.Select(c => Cell(c, inText: false)).ToList(), r.Cells.Select(Value).ToList(), r.Style)).ToList(),
        table.Note is { } note ? Text(note) : null,
        Loc.T("Report_Empty"));

    /// <summary>How a value reads on screen and in the PDF.</summary>
    private static string Cell(object? value, bool inText)
    {
        var culture = Loc.Instance.Culture;
        return value switch
        {
            null => string.Empty,
            Money m => m.Format(culture),
            DateOnly d => inText ? LongDate(d) : d.ToString("d", culture),
            ReportMonth m => Capitalize(m.Month.ToString("MMMM yyyy", culture), culture),
            Share s => s.Format(culture),
            ReportPercent p => p.Value.ToString(culture) + " %",
            ReportText t => Text(t),
            IFormattable f => f.ToString(null, culture),
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>How a value is written to CSV: numbers without currency symbol or thousands separator, ISO dates.</summary>
    private static string Value(object? value)
    {
        var culture = Loc.Instance.Culture;
        return value switch
        {
            Money m => m.ToEuros().ToString("0.00", culture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReportMonth m => m.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Share s => s.Percent.ToString("0.##", culture),
            ReportPercent p => p.Value.ToString(culture),
            _ => Cell(value, inText: false),
        };
    }

    private static string LongDate(DateOnly date) => date.ToString("d MMMM yyyy", Loc.Instance.Culture);

    private static string Capitalize(string text, CultureInfo culture) =>
        text.Length == 0 ? text : char.ToUpper(text[0], culture) + text[1..];

    /// <summary>"Maandoverzicht 2026-01-01 - 2026-10-31": the title and period, without characters Windows refuses.</summary>
    private static string FileName(string title, DateOnly from, DateOnly to)
    {
        var name = $"{title} {from:yyyy-MM-dd} - {to:yyyy-MM-dd}";
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        return new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
    }
}
