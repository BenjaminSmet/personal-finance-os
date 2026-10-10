using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 5 through the real UI: budgets and analysis, the full dashboard, and category management.</summary>
[Collection("UI")]
public class BudgetsUiTests
{
    private static DateOnly ThisMonth
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            return new DateOnly(today.Year, today.Month, 1);
        }
    }

    /// <summary>Three complete months of fake spending and income, and some of this month, recorded on the app's own database.</summary>
    private static async Task RecordHistoryAsync(TempDataFolder temp)
    {
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var audit = new AuditLog(db, clock);
        var accounts = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var ledger = new LedgerService(new TransactionRepository(db), accounts, categories, audit, new UndoHistory(), clock);
        var list = await new AccountService(accounts, audit, clock).ListAsync();
        var current = list.Single(a => a.Name == "Zichtrekening");
        var joint = list.Single(a => a.Name == "Gezamenlijke rekening");
        var cats = await categories.ListAsync(includeArchived: false);
        Guid Cat(string name) => cats.Single(c => c.Name == name).Id;

        Task Add(Guid account, DateOnly date, long cents, TransactionType type, string category, string text) =>
            ledger.RecordAsync(new TransactionRequest(account, date, Money.FromCents(cents), type, text, Cat(category)));

        for (var i = 3; i >= 1; i--)
        {
            var m = ThisMonth.AddMonths(-i);
            await Add(current.Id, m.AddDays(0), 240_000, TransactionType.Income, "Loon", "Loon");
            await Add(current.Id, m.AddDays(2), 28_000, TransactionType.Expense, "Boodschappen", "Supermarkt");
            await Add(current.Id, m.AddDays(4), 6_000, TransactionType.Expense, "Vrije tijd", "Cinema");
            await Add(current.Id, m.AddDays(6), 9_000, TransactionType.Expense, "Vervoer", "Tankstation");
            await Add(joint.Id, m.AddDays(8), 12_000, TransactionType.Expense, "Boodschappen", "Markt");
        }

        await Add(current.Id, ThisMonth, 240_000, TransactionType.Income, "Loon", "Loon");
        await Add(current.Id, ThisMonth, 31_500, TransactionType.Expense, "Boodschappen", "Supermarkt");
        await Add(current.Id, ThisMonth, 19_000, TransactionType.Expense, "Vrije tijd", "Concert");
        await Add(joint.Id, ThisMonth, 8_000, TransactionType.Expense, "Boodschappen", "Markt");

        var transactions = new TransactionRepository(db);
        var recurring = new RecurringService(new RecurringRepository(db), accounts, categories, transactions, ledger, audit, clock);
        await recurring.CreateRuleAsync(new RecurringRuleRequest(
            "Huur", RecurringKind.Expense, current.Id, null, Cat("Wonen"), Money.FromCents(85_000), RecurrenceFrequency.Monthly, DateOnly.FromDateTime(DateTime.Today).AddDays(5)));
    }

    private static async Task<(Avalonia.Controls.Window Window, MainWindowViewModel Vm)> StartWithHistoryAsync(TempDataFolder temp)
    {
        await SeedAsync(temp);
        var (window, vm) = Start(temp);

        // The starter categories are created when the app opens; the history uses them.
        var settings = Navigate<SettingsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Count > 0);
        await RecordHistoryAsync(temp);
        return (window, vm);
    }

    [AvaloniaFact]
    public async Task Budgets_are_set_per_month_and_the_analysis_compares_with_complete_months()
    {
        using var temp = new TempDataFolder();
        var (window, vm) = await StartWithHistoryAsync(temp);
        var budgets = Navigate<BudgetsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => budgets.Rows.Any(r => r.Name == "Boodschappen" && r.Line.Actual.Cents == 39_500));

        Assert.True(budgets.IsCurrentMonth);
        Assert.Contains("3", budgets.ComparisonText, StringComparison.Ordinal); // three complete months
        Assert.Null(budgets.UnusualNote); // enough history to point out unusual changes
        Assert.True(budgets.Rows.Single(r => r.Name == "Vrije tijd").IsUnusual); // 190 against 60 on average
        Assert.False(budgets.Rows.Single(r => r.Name == "Vervoer").IsUnusual); // lower, but the month is not over
        Assert.Single(budgets.SharedLines);
        Assert.Equal(6, budgets.Trend.Count);

        var groceries = budgets.Rows.Single(r => r.Name == "Boodschappen");
        groceries.EditBudgetText = "400";
        await groceries.SaveBudgetCommand.ExecuteAsync(null);
        Assert.Null(groceries.Error);
        HeadlessSetup.PumpUntil(() => budgets.Rows.Single(r => r.Name == "Boodschappen").HasBudget);
        Assert.StartsWith("Budget voor Boodschappen opgeslagen", budgets.StatusMessage, StringComparison.Ordinal);

        var leisure = budgets.Rows.Single(r => r.Name == "Vrije tijd");
        leisure.EditBudgetText = "100";
        await leisure.SaveBudgetCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => budgets.Rows.Single(r => r.Name == "Vrije tijd").IsOver);
        Assert.Contains("90", budgets.Rows.Single(r => r.Name == "Vrije tijd").StatusText, StringComparison.Ordinal); // €90 over budget, in words
        Assert.Equal(2, temp.Scalar("SELECT COUNT(*) FROM budgets;"));

        window.Height = 2600;
        Save(window, "22-budgets");

        // The month before keeps having no budget: setting one never rewrites history.
        await budgets.PreviousMonthCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => !budgets.IsCurrentMonth && budgets.Rows.Count > 0);
        Assert.False(budgets.Rows.Single(r => r.Name == "Boodschappen").HasBudget);
        Assert.Null(budgets.MonthNote); // a complete month
        await budgets.ThisMonthCommand.ExecuteAsync(null);
        Assert.True(budgets.IsCurrentMonth);

        // The dashboard shows the budget and what is over it, with a way to go there.
        var dashboard = Navigate<DashboardViewModel>(vm);
        HeadlessSetup.PumpUntil(() => dashboard.BudgetText is not null && dashboard.HasAttention);
        Assert.True(dashboard.BudgetIsOver);
        var over = Assert.Single(dashboard.Attention, a => a.Target == DashboardTarget.Budgets);
        Assert.Contains("Vrije tijd", over.Text, StringComparison.Ordinal);
        Assert.True(dashboard.HasSavings);
        Assert.True(dashboard.HasCards);
        Assert.Equal(3, dashboard.CashFlows.Count);
        Assert.Contains(dashboard.Upcoming, u => u.Name == "Huur");
        window.Height = 2200;
        Save(window, "23-dashboard");

        over.OpenCommand.Execute(null);
        Assert.Same(budgets, vm.CurrentPage);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Categories_are_reordered_merged_and_brought_back_in_settings()
    {
        using var temp = new TempDataFolder();
        var (window, vm) = await StartWithHistoryAsync(temp);
        var settings = Navigate<SettingsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Count > 0);

        var expenseNames = () => settings.CategoryRows.Where(r => r.Category.Kind == CategoryKind.Expense).Select(r => r.Name).ToList();
        Assert.False(settings.CategoryRows.First().CanMoveUp);
        var before = expenseNames();
        var second = settings.CategoryRows.Single(r => r.Name == before[1]);
        await second.MoveUpCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => expenseNames()[0] == before[1]);

        var leisure = settings.CategoryRows.Single(r => r.Name == "Vrije tijd");
        await leisure.MergeCommand.ExecuteAsync(null);
        Assert.True(settings.IsMerging);
        Assert.DoesNotContain(settings.MergeTargets, t => t.Name == "Vrije tijd" || t.Name == "Loon"); // same kind, never itself
        settings.MergeTarget = settings.MergeTargets.Single(t => t.Name == "Shopping");
        window.Height = 1700;
        Save(window, "24-settings-merge");
        await settings.ConfirmMergeCommand.ExecuteAsync(null);

        Assert.Equal("\"Vrije tijd\" is samengevoegd met \"Shopping\".", settings.CategoryMessage);
        Assert.False(settings.IsMerging);
        Assert.DoesNotContain(settings.CategoryRows, r => r.Name == "Vrije tijd");
        var archived = Assert.Single(settings.ArchivedCategoryRows);
        Assert.Equal(0, temp.Scalar($"SELECT COUNT(*) FROM transactions WHERE category_id = '{archived.Category.Id:D}';"));

        await archived.ReactivateCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Any(r => r.Name == "Vrije tijd"));
        Assert.False(settings.HasArchivedCategories);

        // The budgets page shows the merged spending under the target.
        var budgets = Navigate<BudgetsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => budgets.Rows.Any(r => r.Name == "Shopping" && r.Line.Actual.Cents == 19_000));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Budgets_in_dark_and_english()
    {
        using var temp = new TempDataFolder();
        var (window, vm) = await StartWithHistoryAsync(temp);
        try
        {
            var budgets = Navigate<BudgetsViewModel>(vm);
            HeadlessSetup.PumpUntil(() => budgets.Rows.Count > 0);
            ThemeManager.Apply(AppTheme.Dark);
            Loc.Instance.SetLanguage(AppLanguage.English);
            HeadlessSetup.PumpUntil(() => budgets.Rows.Any(r => r.Name == "Without category") || budgets.ComparisonText.StartsWith("Average", StringComparison.Ordinal));
            window.Height = 2600;
            Save(window, "dark-11-budgets-english");

            var dashboard = Navigate<DashboardViewModel>(vm);
            HeadlessSetup.PumpUntil(() => dashboard.CashFlows.Count == 3 && dashboard.CashFlows[0].Title.StartsWith("Next", StringComparison.Ordinal));
            window.Height = 2200;
            Save(window, "dark-12-dashboard-english");
        }
        finally
        {
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
            window.Close();
        }
    }
}
