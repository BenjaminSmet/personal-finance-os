using System.Text;
using Avalonia.Headless.XUnit;
using Microsoft.Data.Sqlite;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Infrastructure.Reports;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// Phase 8 reports (spec §18, §23) against a real migrated database. The fixture: history since 1 August, today is
/// 10 October. Salary €2,400 and rent €900 in August and September; groceries on the current account, on the VISA
/// (with a refund) and on a 50/50 joint account; the card statements paid by transfer on the 5th.
/// The export test switches the app language, so these tests share the UI collection and never run beside a UI test.
/// </summary>
[Collection("UI")]
public sealed class ReportTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 8, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly CategoryService _categories;
    private readonly BudgetService _budgets;
    private readonly ReconciliationService _reconciliation;
    private readonly NetWorthService _netWorth;
    private readonly ReportService _reports;

    public ReportTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var budgetRepository = new BudgetRepository(db);
        var transactions = new TransactionRepository(db);
        var ownership = new OwnershipRepository(db);
        var reconciliations = new ReconciliationRepository(db);
        var settings = new AppSettingsService(new SettingsStore(db, _clock));
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _categories = new CategoryService(categoryRepository, budgetRepository, new SettingsStore(db, _clock), audit, _clock);
        _budgets = new BudgetService(budgetRepository, categoryRepository, audit, _clock);
        _reconciliation = new ReconciliationService(_ledger, accountRepository, reconciliations, audit, _clock);
        _recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, _recurring, audit, _clock);
        var forecast = new ForecastService(accountRepository, _ledger, _recurring, cards, settings, new SpendingProfileService(accountRepository, categoryRepository, transactions, new CreditCardRepository(db), _recurring, _clock), _clock);
        var assetRepository = new AssetRepository(db);
        var valuations = new ValuationRepository(db);
        _netWorth = new NetWorthService(accountRepository, transactions, assetRepository, valuations, ownership, _clock);
        _reports = new ReportService(accountRepository, categoryRepository, transactions, budgetRepository, reconciliations, ownership, _recurring, forecast, cards, _netWorth, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static Money Euro(long euros) => Money.FromCents(euros * 100);

    private sealed record Fixture(Account Current, Account Joint, Account Visa, Account Savings, Category Groceries, Category Housing);

    private async Task<Fixture> SetUpAsync()
    {
        var current = await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Euro(1_000), Share.Full));
        var joint = await _accounts.CreateAsync(new CreateAccountRequest("Gezamenlijke rekening", AccountType.JointCurrent, Opening, Money.Zero, Share.FromPercent(50)));
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Euro(1_500), 28, 5, current.Id)));
        var savings = await _accounts.CreateAsync(new CreateAccountRequest("Spaarrekening", AccountType.Savings, Opening, Euro(5_000), Share.Full));

        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var housing = await _categories.CreateAsync("Wonen", CategoryKind.Expense);
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);

        foreach (var (month, food) in new[] { (8, 300L), (9, 400L) })
        {
            await Record(current, new(2026, month, 1), 2_400, TransactionType.Income, salary);
            await Record(current, new(2026, month, 3), 900, TransactionType.Expense, housing);
            await Record(current, new(2026, month, 8), food, TransactionType.Expense, groceries);
        }

        // VISA: period 28 Jul - 27 Aug has €200; period 28 Aug - 27 Sep has €120 minus a €50 refund. Paid on the 5th.
        await Record(visa, new(2026, 8, 10), 200, TransactionType.Expense, groceries);
        await Record(visa, new(2026, 9, 2), 50, TransactionType.Refund, groceries);
        await Record(visa, new(2026, 9, 15), 120, TransactionType.Expense, groceries);
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, new(2026, 9, 5), Euro(200), "VISA augustus"));
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, new(2026, 10, 5), Euro(70), "VISA september"));

        // Joint account, 50/50.
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, joint.Id, new(2026, 9, 1), Euro(600), "Gezamenlijke pot"));
        await Record(joint, new(2026, 9, 10), 300, TransactionType.Expense, groceries);

        await Record(current, new(2026, 10, 4), 270, TransactionType.Expense, groceries);
        await _budgets.SetAsync(groceries.Id, new(2026, 9, 1), Euro(500));
        await _reconciliation.RecordAsync(current.Id, new(2026, 9, 30), Euro(2_500), addAdjustment: false, reason: null);

        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, housing.Id, Euro(900), RecurrenceFrequency.Monthly, new(2026, 10, 15)));
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, salary.Id, Euro(2_400), RecurrenceFrequency.Monthly, new(2026, 10, 25)));
        return new Fixture(current, joint, visa, savings, groceries, housing);
    }

    private Task<Transaction> Record(Account account, DateOnly date, long euros, TransactionType type, Category category) =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, date, Euro(euros), type, category.Name, category.Id));

    private Task<Report> Generate(ReportType type, DateOnly from, DateOnly to, IReadOnlyList<Guid>? accounts = null, IReadOnlyList<Guid>? categories = null, bool planned = false) =>
        _reports.GenerateAsync(new ReportRequest(type, from, to, accounts, categories, planned));

    private static ReportTable Table(Report report, string key) => report.Tables.Single(t => t.Title.Key == key);

    private static ReportRow RowFor(ReportTable table, object first) => table.Rows.Single(r => Equals(r.Cells[0], first));

    private static bool HasNote(Report report, string key) => report.Notes.Any(n => n.Key == key);

    // ===== Reconciles with the ledger =====

    [Fact]
    public async Task The_monthly_summary_matches_the_ledger_and_counts_a_card_purchase_once()
    {
        await SetUpAsync();
        var report = await Generate(ReportType.MonthlySummary, new(2026, 8, 1), new(2026, 10, 31));
        var perMonth = Table(report, "Report_Table_PerMonth");

        // August: 900 rent + 300 groceries + 200 on the VISA. Paying the card on 5 September is a transfer, not spending.
        Assert.Equal([Euro(2_400), Euro(1_400), Euro(1_000)], RowFor(perMonth, new ReportMonth(new(2026, 8, 1))).Cells.Skip(1).Take(3));

        // September: 900 + 400 + 120 on the card - 50 refund + 300 on the joint account.
        Assert.Equal([Euro(2_400), Euro(1_670), Euro(730)], RowFor(perMonth, new ReportMonth(new(2026, 9, 1))).Cells.Skip(1).Take(3));
        var october = RowFor(perMonth, new ReportMonth(new(2026, 10, 1)));
        Assert.Equal([Money.Zero, Euro(270)], october.Cells.Skip(1).Take(2));
        Assert.Equal(new ReportText("Report_UntilToday", new DateOnly(2026, 10, 10)).Key, ((ReportText)october.Cells[4]!).Key);

        // The total equals the authoritative ledger totals.
        var all = await new TransactionRepository(() => new FinanceDbContext(FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile))).ListAsync(new TransactionQuery());
        var ledger = PersonalFinanceOS.Domain.Ledger.LedgerMath.Totals(all, new(2026, 8, 1), new(2026, 10, 31));
        var total = perMonth.Rows[^1];
        Assert.Equal(RowStyle.Total, total.Style);
        Assert.Equal([ledger.Income, ledger.Expenses, ledger.Net], total.Cells.Skip(1).Take(3));
        Assert.Equal(Euro(3_340), ledger.Expenses);

        // Groceries per category: 300 + 400 + 200 + 120 - 50 + 300 + 270.
        var groceries = RowFor(Table(report, "Report_Table_SpendingByCategory"), "Boodschappen");
        Assert.Equal(Euro(1_540), groceries.Cells[1]);
        Assert.True(HasNote(report, "Report_Note_PlannedExcluded"));
        Assert.True(HasNote(report, "Report_Note_NoComparison"));
    }

    [Fact]
    public async Task Planned_items_are_shown_apart_and_never_added_to_actual_totals()
    {
        await SetUpAsync();
        var actual = await Generate(ReportType.MonthlySummary, new(2026, 10, 1), new(2026, 10, 31));
        var forecast = await Generate(ReportType.MonthlySummary, new(2026, 10, 1), new(2026, 10, 31), planned: true);

        Assert.Equal(Table(actual, "Report_Table_PerMonth").Rows.SelectMany(r => r.Cells), Table(forecast, "Report_Table_PerMonth").Rows.SelectMany(r => r.Cells));
        Assert.DoesNotContain(actual.Tables, t => t.Title.Key == "Report_Table_Planned");
        var planned = Table(forecast, "Report_Table_Planned");
        Assert.All(planned.Rows, r => Assert.Equal(RowStyle.Subtle, r.Style));
        Assert.Contains(planned.Rows, r => (string)r.Cells[1]! == "Huur" && (Money)r.Cells[4]! == Euro(-900));
        Assert.Contains(planned.Rows, r => (string)r.Cells[1]! == "Loon" && (Money)r.Cells[4]! == Euro(2_400));

        // Cash flow: actual up to today; the projection is a separate, labelled estimate.
        var cash = await Generate(ReportType.CashFlow, new(2026, 10, 1), new(2026, 10, 31), planned: true);
        var actualRow = RowFor(Table(cash, "Report_Table_CashPerAccount"), "Zichtrekening");
        Assert.Equal([Euro(2_500), Money.Zero, Euro(340), Euro(2_160)], actualRow.Cells.Skip(1));
        var projected = RowFor(Table(cash, "Report_Table_Projection"), "Zichtrekening");
        Assert.Equal(RowStyle.Subtle, projected.Style);
        // Minus the usual day-to-day spending for the rest of October: €485 a month (the middle of August and September), 21 of 31 days.
        Assert.Equal(Euro(2_160 - 900 + 2_400) - Money.FromCents(32_855), projected.Cells[5]);

        var withoutPlanned = await Generate(ReportType.CashFlow, new(2026, 10, 1), new(2026, 10, 31));
        Assert.Single(withoutPlanned.Tables);
        Assert.True(HasNote(withoutPlanned, "Report_Note_FutureWithoutPlanned"));
    }

    [Fact]
    public async Task The_account_report_has_a_running_balance_that_ends_on_the_ledger_balance_and_shows_reconciliation()
    {
        var f = await SetUpAsync();
        var report = await Generate(ReportType.Account, new(2026, 8, 1), new(2026, 9, 30), [f.Current.Id]);
        Assert.Equal("Report_Title_Account", report.Title.Key);
        var lines = Table(report, "Report_Table_Transactions");
        Assert.Equal(Euro(1_000), lines.Rows[0].Cells[5]);
        Assert.Equal(await _ledger.GetBalanceAsync(f.Current.Id, new(2026, 9, 30)), lines.Rows[^1].Cells[5]);
        Assert.Equal(Euro(2_500), lines.Rows[^1].Cells[5]);
        Assert.Contains(lines.Rows, r => r.Cells[3] is ReportText { Key: "EntryKind_CardSettlement" } && (Money)r.Cells[4]! == Euro(-200));

        var reconciliation = Table(report, "Report_Table_Reconciliation");
        Assert.Equal(new ReportText("Report_Rec_Matched").Key, ((ReportText)Assert.Single(reconciliation.Rows).Cells[4]!).Key);
        Assert.True(HasNote(report, "Report_Note_ReconciledOn"));

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Generate(ReportType.Account, new(2026, 8, 1), new(2026, 9, 30)));
        Assert.Equal("report.account.required", error.Code);
    }

    [Fact]
    public async Task The_card_report_separates_actual_statements_from_the_running_estimate_without_double_counting()
    {
        var f = await SetUpAsync();
        var report = await Generate(ReportType.CreditCard, new(2026, 8, 1), new(2026, 10, 31));
        var statements = Table(report, "Report_Table_Statements");
        Assert.Equal(3, statements.Rows.Count);

        // 28 Jul - 27 Aug: €200 bought, €200 paid on 5 September.
        Assert.Equal([Euro(200), Money.Zero, Euro(200)], statements.Rows[0].Cells.Skip(1).Take(3));
        Assert.Equal([Euro(200), Money.Zero, new DateOnly(2026, 9, 5)], statements.Rows[0].Cells.Skip(5));

        // 28 Aug - 27 Sep: €120 bought, €50 refunded, statement €70 paid on 5 October.
        Assert.Equal([Euro(120), Euro(50), Euro(70)], statements.Rows[1].Cells.Skip(1).Take(3));
        Assert.Equal("Report_Basis_Actual", ((ReportText)statements.Rows[1].Cells[4]!).Key);
        Assert.Equal(Money.Zero, statements.Rows[1].Cells[6]);

        // The running period is an estimate.
        Assert.Equal(RowStyle.Subtle, statements.Rows[2].Style);
        Assert.Equal("Report_Basis_Estimate", ((ReportText)statements.Rows[2].Cells[4]!).Key);

        var now = Table(report, "Report_Table_CardNow");
        Assert.Equal(Euro(1_500), now.Rows[0].Cells[1]);
        Assert.Equal(Money.Zero, now.Rows[1].Cells[1]);
        Assert.True(HasNote(report, "Report_Note_CardNoDoubleCount"));

        // Spending on the card is in the monthly summary once; the payments are not.
        var summary = await Generate(ReportType.MonthlySummary, new(2026, 8, 1), new(2026, 9, 30), [f.Visa.Id]);
        Assert.Equal(Euro(270), Table(summary, "Report_Table_PerMonth").Rows[^1].Cells[2]);
    }

    [Fact]
    public async Task The_joint_report_shows_the_full_activity_and_your_part_apart()
    {
        await SetUpAsync();
        var report = await Generate(ReportType.JointFinance, new(2026, 8, 1), new(2026, 9, 30));
        var summary = Table(report, "Report_Table_JointSummary");
        Assert.Equal("Gezamenlijke rekening", summary.Title.Args[0]);
        Assert.Equal([Euro(600), Euro(300)], RowFor(summary, new ReportText("Report_Col_In")).Cells.Skip(1));
        Assert.Equal([Euro(300), Euro(150)], summary.Rows[^1].Cells.Skip(1));
        var activity = Table(report, "Report_Table_JointActivity");
        Assert.Equal(Share.FromPercent(50), activity.Rows[1].Cells[4]);
        Assert.Equal(Euro(-150), activity.Rows[1].Cells[5]);
    }

    [Fact]
    public async Task Budget_net_worth_and_annual_reports_use_the_same_figures_as_their_pages()
    {
        var f = await SetUpAsync();
        var budget = await Generate(ReportType.Budget, new(2026, 9, 1), new(2026, 9, 30));
        var groceries = RowFor(Table(budget, "Report_Table_BudgetVsActual"), "Boodschappen");

        // September groceries: 400 + 120 - 50 + 300.
        Assert.Equal([Euro(500), Euro(770), Euro(-270)], groceries.Cells.Skip(1).Take(3));
        Assert.Equal("Report_Over", ((ReportText)groceries.Cells[4]!).Key);
        var housing = RowFor(Table(budget, "Report_Table_BudgetVsActual"), "Wonen");
        Assert.Equal(RowStyle.Subtle, housing.Style);

        var worth = await Generate(ReportType.NetWorth, new(2026, 8, 1), new(2026, 10, 31));
        var lines = Table(worth, "Report_Table_NetWorthOn");
        Assert.Equal((await _netWorth.CalculateAsync()).Total, lines.Rows[^1].Cells[4]);
        Assert.Equal(Euro(2_160 + 5_000 + 150), lines.Rows[^1].Cells[4]);
        Assert.Equal(3, Table(worth, "Report_Table_NetWorthHistory").Rows.Count);

        // Only months with data, and the year total equals the summary.
        var annual = await Generate(ReportType.AnnualOverview, new(2026, 1, 1), new(2026, 12, 31));
        var year = Table(annual, "Report_Table_Year");
        Assert.Equal(5, year.Rows.Count); // August, September, October, total, average
        Assert.True(HasNote(annual, "Report_Note_BeforeHistory"));
        Assert.True(HasNote(annual, "Report_Note_FutureMonths"));
        var summary = await Generate(ReportType.MonthlySummary, new(2026, 8, 1), new(2026, 10, 31));
        Assert.Equal(Table(summary, "Report_Table_PerMonth").Rows[^1].Cells.Skip(1).Take(3), year.Rows[3].Cells.Skip(1).Take(3));
        Assert.Equal(f.Housing.Name, Table(annual, "Report_Table_SpendingByCategoryYear").Rows[0].Cells[0]);
    }

    [Fact]
    public async Task A_period_must_be_valid()
    {
        await SetUpAsync();
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => Generate(ReportType.MonthlySummary, new(2026, 10, 1), new(2026, 9, 1)));
        Assert.Equal("report.period.invalid", error.Code);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => Generate(ReportType.MonthlySummary, new(2000, 1, 1), new(2026, 9, 1)));
        Assert.Equal("report.period.tooLong", error.Code);
    }

    // ===== Generating and exporting never change data; exports match the screen =====

    [Fact]
    public async Task Generating_and_exporting_every_report_does_not_change_the_database()
    {
        var f = await SetUpAsync();
        var before = Dump();
        foreach (var type in Enum.GetValues<ReportType>())
        {
            foreach (var planned in new[] { false, true })
            {
                var report = await Generate(type, new(2026, 1, 1), new(2026, 12, 31), type == ReportType.Account ? [f.Current.Id] : null, planned: planned);
                var formatted = ReportFormatter.Format(report);
                Assert.NotEmpty(PdfReportWriter.Write(formatted));
                Assert.NotEmpty(CsvReportWriter.Write(formatted));
            }
        }

        Assert.Equal(before, Dump());
    }

    /// <summary>Runs on the UI thread because switching the language updates the app's open windows.</summary>
    [AvaloniaFact]
    public async Task The_pdf_and_csv_contain_exactly_the_values_shown_on_screen()
    {
        var f = await SetUpAsync();
        foreach (var language in new[] { AppLanguage.Dutch, AppLanguage.English })
        {
            Loc.Instance.SetLanguage(language);
            try
            {
                foreach (var type in Enum.GetValues<ReportType>())
                {
                    var report = await Generate(type, new(2026, 8, 1), new(2026, 10, 31), type == ReportType.Account ? [f.Current.Id] : null, planned: true);
                    var screen = ReportFormatter.Format(report);
                    Assert.DoesNotContain("[Report", screen.Title, StringComparison.Ordinal);
                    var pdf = Encoding.Latin1.GetString(PdfReportWriter.Write(screen));
                    var csv = Encoding.UTF8.GetString(CsvReportWriter.Write(screen));
                    Assert.StartsWith("%PDF-1.4", pdf, StringComparison.Ordinal);
                    Assert.EndsWith("%%EOF\n", pdf, StringComparison.Ordinal);
                    var csvLines = csv.TrimStart('﻿').Split("\r\n");
                    foreach (var table in screen.Tables)
                    {
                        Assert.DoesNotContain("[Report", table.Title, StringComparison.Ordinal);
                        Assert.Contains(string.Join(screen.CsvSeparator, table.Columns.Select(c => CsvReportWriter.Field(c.Header, screen.CsvSeparator))), csvLines);
                        foreach (var row in table.Rows)
                        {
                            Assert.Contains(string.Join(screen.CsvSeparator, row.Values.Select(v => CsvReportWriter.Field(v, screen.CsvSeparator))), csvLines);
                            for (var i = 0; i < table.Columns.Count; i++)
                            {
                                // Numbers never wrap, so each amount appears in the PDF exactly as on screen.
                                if (table.Columns[i].Align == ColumnAlign.Right && row.Cells[i].Length > 0)
                                {
                                    Assert.Contains("(" + PdfReportWriter.Escape(row.Cells[i]) + ")", pdf, StringComparison.Ordinal);
                                }

                                Assert.DoesNotContain("[Report", row.Cells[i], StringComparison.Ordinal);
                            }
                        }
                    }
                }
            }
            finally
            {
                Loc.Instance.SetLanguage(AppLanguage.Dutch);
            }
        }
    }

    [Fact]
    public void Csv_numbers_use_the_language_and_text_that_looks_like_a_formula_is_neutralised()
    {
        Assert.Equal("-12,50", CsvReportWriter.Field("-12,50", ';'));
        Assert.Equal("'=HYPERLINK(1)", CsvReportWriter.Field("=HYPERLINK(1)", ';'));
        Assert.Equal("\"Huur; juni\"", CsvReportWriter.Field("Huur; juni", ';'));
        Assert.Equal("\"Zeg \"\"hallo\"\"\"", CsvReportWriter.Field("Zeg \"hallo\"", ','));
        Assert.Equal(@"\200 1.234,56 \(caf\351\)", PdfReportWriter.Escape("€ 1.234,56 (café)"));
    }

    /// <summary>Every row of every table, so any change to the database is seen.</summary>
    private string Dump()
    {
        using var connection = _temp.Open();
        connection.Open();
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var sb = new StringBuilder();
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY 1;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sb.Append(table);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    sb.Append('|').Append(reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                }

                sb.Append('\n');
            }
        }

        SqliteConnection.ClearAllPools();
        return sb.ToString();
    }
}
