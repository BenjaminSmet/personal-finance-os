using System.Text;
using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Reports;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 8 through the real UI: the Reports page and saving a report as PDF and CSV.</summary>
[Collection("UI")]
public class ReportsUiTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private static string OutputDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("PFOS_SCREENSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Two months of salary, rent and groceries, a VISA purchase paid from the current account, and planned rent.</summary>
    private static async Task SeedPhase8Async(TempDataFolder temp)
    {
        await SeedAsync(temp);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var audit = new AuditLog(db, clock);
        var accounts = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        var ledger = new LedgerService(transactions, accounts, categories, audit, new UndoHistory(), clock);
        var recurring = new RecurringService(new RecurringRepository(db), accounts, categories, transactions, ledger, audit, clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accounts, transactions, recurring, audit, clock);
        var list = await accounts.ListAsync(false);
        var current = list.Single(a => a.Name == "Zichtrekening");
        var joint = list.Single(a => a.Name == "Gezamenlijke rekening");
        var visa = list.Single(a => a.Name == "VISA");
        await new CategoryService(categories, new BudgetRepository(db), new SettingsStore(db, clock), audit, clock).EnsureDefaultsAsync(DefaultCategories.Localized());
        var all = await categories.ListAsync(false);
        Guid Category(string name, CategoryKind kind) => all.First(c => c.Kind == kind && c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).Id;
        var groceries = Category("Boodschappen", CategoryKind.Expense);
        var housing = Category("Wonen", CategoryKind.Expense);
        var salary = Category("Loon", CategoryKind.Income);

        await cards.SaveSettingsAsync(visa.Id, new CardSettings(Money.FromCents(150_000), 28, 5, current.Id));
        var month = new DateOnly(Today.Year, Today.Month, 1);
        foreach (var start in new[] { month.AddMonths(-1), month })
        {
            await ledger.RecordAsync(new TransactionRequest(current.Id, start, Money.FromCents(245_000), TransactionType.Income, "Loon", salary));
            await ledger.RecordAsync(new TransactionRequest(current.Id, start.AddDays(1), Money.FromCents(92_500), TransactionType.Expense, "Huur", housing));
            await ledger.RecordAsync(new TransactionRequest(joint.Id, start.AddDays(2), Money.FromCents(18_640), TransactionType.Expense, "Colruyt", groceries));
        }

        await ledger.RecordAsync(new TransactionRequest(visa.Id, month.AddMonths(-1).AddDays(9), Money.FromCents(12_999), TransactionType.Expense, "Fnac (café-boek)", groceries));
        await ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, month.AddMonths(-1).AddDays(4), Money.FromCents(31_240), "VISA afrekening"));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, housing, Money.FromCents(92_500), RecurrenceFrequency.Monthly, Today.AddDays(3) < month.AddMonths(1) ? Today.AddDays(3) : month.AddMonths(1).AddDays(1)));
        await new AppSettingsService(new SettingsStore(db, clock)).SetMinimumBufferAsync(Money.FromCents(50_000));
    }

    [AvaloniaFact]
    public async Task A_report_is_made_on_screen_and_saved_as_pdf_and_csv_where_the_user_chooses()
    {
        using var temp = new TempDataFolder();
        await SeedPhase8Async(temp);
        var (window, vm) = Start(temp);
        var transactions = temp.Scalar("SELECT COUNT(*) FROM transactions;");
        var reports = Navigate<ReportsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => reports.AccountChoices.Count == 4 && reports.CategoryChoices.Count > 0);
        Assert.False(reports.HasReport);
        Assert.Equal(8, reports.Types.Count);
        Save(window, "43-reports-start");

        // Last 12 months: the monthly summary, with planned items shown apart.
        reports.SelectedPeriod = reports.Periods.Single(p => p.Period == ReportPeriod.Last12Months);
        reports.IncludePlanned = true;
        await reports.GenerateCommand.ExecuteAsync(null);
        Assert.Null(reports.Error);
        var report = Assert.IsType<FormattedReport>(reports.Formatted);
        Assert.Equal("Maandoverzicht", report.Title);
        Assert.Contains(report.Tables, t => t.Title.StartsWith("Geplande posten", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("Maanden vóór", StringComparison.Ordinal));
        window.Height = 2400;
        Save(window, "44-report-monthly");

        // Saved where the test (the user) chooses; exactly what is on screen.
        var saved = new Dictionary<ReportFileKind, string>();
        reports.SaveTarget = (name, kind) =>
        {
            var path = Path.Combine(OutputDirectory, name + (kind == ReportFileKind.Pdf ? ".pdf" : ".csv"));
            saved[kind] = path;
            return Task.FromResult<Stream?>(File.Create(path));
        };
        await reports.SavePdfCommand.ExecuteAsync(null);
        await reports.SaveCsvCommand.ExecuteAsync(null);
        Assert.Null(reports.Error);
        Assert.StartsWith("Bewaard als ", reports.StatusMessage, StringComparison.Ordinal);
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(File.ReadAllBytes(saved[ReportFileKind.Pdf]))[..8], StringComparison.Ordinal);
        var csv = File.ReadAllText(saved[ReportFileKind.Csv], Encoding.UTF8);
        Assert.Contains("Maand;Inkomsten;Uitgaven;Netto;Opmerking", csv, StringComparison.Ordinal);
        Assert.Contains("2450,00", csv, StringComparison.Ordinal);

        // A cancelled save dialog does nothing.
        reports.SaveTarget = (_, _) => Task.FromResult<Stream?>(null);
        reports.StatusMessage = null;
        await reports.SavePdfCommand.ExecuteAsync(null);
        Assert.Null(reports.StatusMessage);

        // Changing a choice marks the report as outdated until it is made again.
        reports.SelectedType = reports.Types.Single(t => t.Type == ReportType.Account);
        Assert.True(reports.IsOutdated);
        Assert.True(reports.NeedsSingleAccount);
        reports.SelectedAccount = reports.SingleAccounts.Single(a => a.Name == "Zichtrekening");
        reports.SelectedPeriod = reports.Periods.Single(p => p.Period == ReportPeriod.ThisMonth);
        await reports.GenerateCommand.ExecuteAsync(null);
        Assert.False(reports.IsOutdated);
        Assert.Equal("Rekeningrapport: Zichtrekening", reports.Formatted!.Title);
        window.Height = 1700;
        Save(window, "45-report-account");

        reports.SelectedType = reports.Types.Single(t => t.Type == ReportType.CreditCard);
        reports.SelectedPeriod = reports.Periods.Single(p => p.Period == ReportPeriod.Last12Months);
        await reports.GenerateCommand.ExecuteAsync(null);
        window.Height = 1900;
        Save(window, "46-report-visa");
        reports.SaveTarget = (name, kind) => Task.FromResult<Stream?>(File.Create(Path.Combine(OutputDirectory, name + ".pdf")));
        await reports.SavePdfCommand.ExecuteAsync(null);

        // Typed dates make the period custom; an impossible period is refused.
        reports.FromDate = new DateTime(2026, 5, 1);
        Assert.Equal(ReportPeriod.Custom, reports.SelectedPeriod.Period);
        reports.ToDate = new DateTime(2026, 4, 1);
        await reports.GenerateCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Error("report.period.invalid"), reports.Error);

        // Reports never change data.
        Assert.Equal(transactions, temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        try
        {
            ThemeManager.Apply(AppTheme.Dark);
            Loc.Instance.SetLanguage(AppLanguage.English);
            reports.SelectedType = reports.Types.Single(t => t.Type == ReportType.NetWorth);
            reports.SelectedPeriod = reports.Periods.Single(p => p.Period == ReportPeriod.ThisYear);
            await reports.GenerateCommand.ExecuteAsync(null);
            Assert.Equal("Net worth", reports.Formatted!.Title);
            window.Height = 1500;
            Save(window, "dark-16-report-english");
            reports.SaveTarget = (name, kind) => Task.FromResult<Stream?>(File.Create(Path.Combine(OutputDirectory, name + ".pdf")));
            await reports.SavePdfCommand.ExecuteAsync(null);
        }
        finally
        {
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
            window.Close();
        }
    }
}
