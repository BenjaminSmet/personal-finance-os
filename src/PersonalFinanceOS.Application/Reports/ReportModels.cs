namespace PersonalFinanceOS.Application.Reports;

/// <summary>The report types of spec §18.</summary>
public enum ReportType
{
    MonthlySummary,
    CashFlow,
    Account,
    Budget,
    JointFinance,
    CreditCard,
    NetWorth,
    AnnualOverview,
}

/// <summary>
/// What the user chose (spec §18 report controls). <see cref="AccountIds"/> and <see cref="CategoryIds"/> are null for
/// "all". <see cref="IncludePlanned"/> adds planned items and projections in their own, labelled tables; they are never
/// added to actual totals (spec §23).
/// </summary>
public sealed record ReportRequest(
    ReportType Type,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<Guid>? AccountIds = null,
    IReadOnlyList<Guid>? CategoryIds = null,
    bool IncludePlanned = false);

/// <summary>
/// A localizable text: a string key with values. Values are <see cref="Domain.Money"/>, dates, <see cref="ReportMonth"/>,
/// <see cref="Domain.Share"/>, <see cref="ReportPercent"/>, numbers, other <see cref="ReportText"/>s or the user's own
/// names. The screen and the exports format them the same way.
/// </summary>
public sealed record ReportText(string Key, params object[] Args)
{
    public bool Equals(ReportText? other) => other is not null && Key == other.Key && Args.SequenceEqual(other.Args);

    public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A month, shown as "October 2026".</summary>
public readonly record struct ReportMonth(DateOnly Month);

/// <summary>A whole percentage, shown as "42 %".</summary>
public readonly record struct ReportPercent(int Value);

public enum ColumnAlign
{
    Left,
    Right,
}

public enum RowStyle
{
    Normal,

    /// <summary>A total or subtotal line.</summary>
    Total,

    /// <summary>A line that is shown for context, such as an estimate in a list of actual values.</summary>
    Subtle,
}

public sealed record ReportColumn(ReportText Header, ColumnAlign Align = ColumnAlign.Left);

/// <summary>One line of a table. A cell is any value a <see cref="ReportText"/> can hold, or null for empty.</summary>
public sealed record ReportRow(IReadOnlyList<object?> Cells, RowStyle Style = RowStyle.Normal);

public sealed record ReportTable(ReportText Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows, ReportText? Note = null)
{
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// A generated report: title, the period it covers, when it was made, what it assumes, and its tables. Generating a
/// report only reads data (spec §23). The screen, the PDF and the CSV are all made from this one object, so their values
/// always match.
/// </summary>
public sealed record Report(
    ReportRequest Request,
    ReportText Title,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ReportText> Notes,
    IReadOnlyList<ReportTable> Tables);

/// <summary>
/// A report with every text in the user's language: what the screen shows and what the exports write.
/// <see cref="PageFormat"/> is "Page {0} of {1}" in that language; <see cref="CsvSeparator"/> is ';' where the decimal
/// separator is a comma, so a spreadsheet in that language opens the file as columns.
/// </summary>
public sealed record FormattedReport(
    string Title,
    string Period,
    string Generated,
    IReadOnlyList<string> Notes,
    IReadOnlyList<FormattedTable> Tables,
    string FileName,
    string PageFormat,
    char CsvSeparator);

public sealed record FormattedTable(string Title, IReadOnlyList<FormattedColumn> Columns, IReadOnlyList<FormattedRow> Rows, string? Note, string? EmptyText);

public sealed record FormattedColumn(string Header, ColumnAlign Align);

/// <summary>
/// One line as text. <see cref="Values"/> is what CSV writes: the same numbers without currency symbols or thousands
/// separators so a spreadsheet reads them as numbers.
/// </summary>
public sealed record FormattedRow(IReadOnlyList<string> Cells, IReadOnlyList<string> Values, RowStyle Style);
