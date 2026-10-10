using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.App.Views;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 2 through the real UI: Quick Add, the Transactions page and reconciliation.</summary>
[Collection("UI")]
public class LedgerUiTests
{
    private static readonly DateOnly Opening = new(2026, 1, 1);

    private static string ScreenshotDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("PFOS_SCREENSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static (MainWindow Window, MainWindowViewModel Vm) Start(TempDataFolder temp)
    {
        var app = (PersonalFinanceOS.App.App)Avalonia.Application.Current!;
        var window = Assert.IsType<MainWindow>(app.CreateStartWindow(temp.Paths, _ => { }));
        window.Width = 1280;
        window.Height = 900;
        window.Show();
        return (window, (MainWindowViewModel)window.DataContext!);
    }

    private static void Save(Window window, string name)
    {
        HeadlessSetup.Pump(250);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDirectory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task SeedAsync(TempDataFolder temp)
    {
        Assert.True(temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var service = new AccountService(new AccountRepository(db), new AuditLog(db, clock), clock);
        await service.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Money.FromCents(184_235), Share.Full));
        await service.CreateAsync(new CreateAccountRequest("Gezamenlijke rekening", AccountType.JointCurrent, Opening, Money.FromCents(240_000), new Share(5000)));
        await service.CreateAsync(new CreateAccountRequest("Spaarrekening 1", AccountType.Savings, Opening, Money.FromCents(815_000), Share.Full));
        await service.CreateAsync(new CreateAccountRequest("VISA", AccountType.CreditCard, Opening, Money.FromCents(-31_240), Share.Full));
    }

    private static void Type(QuickAddViewModel quickAdd, string text)
    {
        quickAdd.Text = text;
        HeadlessSetup.PumpUntil(() => quickAdd.Draft?.Input == text.Trim());
    }

    private static T Navigate<T>(MainWindowViewModel vm)
        where T : PageViewModel
    {
        vm.SelectedItem = vm.NavItems.Single(n => n.Page is T);
        return (T)vm.CurrentPage;
    }

    [AvaloniaFact]
    public async Task Clear_quick_add_entry_is_saved_with_enter_and_updates_balances()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        var dashboard = (DashboardViewModel)vm.CurrentPage;
        HeadlessSetup.PumpUntil(() => dashboard.ActiveAccountCount == 4);

        Type(vm.QuickAdd, "Supermarkt 42,80");
        Assert.False(vm.QuickAdd.Draft!.NeedsReview);
        Assert.Contains("Supermarkt", vm.QuickAdd.Summary, StringComparison.Ordinal);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;")); // a preview is not a posting
        Save(window, "10-quick-add-preview");

        // Real keyboard: focus with Ctrl+K, then Enter.
        window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
        HeadlessSetup.Pump();
        Assert.True(window.FindControl<TextBox>("QuickAddBox")!.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        HeadlessSetup.PumpUntil(() => vm.QuickAdd.StatusMessage is not null);

        Assert.Equal(-4280, temp.Scalar("SELECT amount_cents FROM transactions;"));
        Assert.Empty(vm.QuickAdd.Text);
        HeadlessSetup.PumpUntil(() => dashboard.LiquidTotal == Money.FromCents(184_235 + 240_000 - 4_280));
        Assert.Equal(Money.FromCents(4_280), dashboard.MonthTotals.Expenses);
        Save(window, "11-quick-add-saved");

        await vm.QuickAdd.UndoCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => dashboard.LiquidTotal == Money.FromCents(184_235 + 240_000));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Uncertain_entry_opens_the_preview_and_corrections_are_saved()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);

        Type(vm.QuickAdd, "Zetel 1.234");
        Assert.True(vm.QuickAdd.Draft!.NeedsReview);

        await vm.QuickAdd.SubmitCommand.ExecuteAsync(null); // Enter opens the preview, posts nothing
        Assert.True(vm.QuickAdd.IsReviewOpen);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        var editor = vm.QuickAdd.Editor;
        editor.SelectedAccount = editor.AccountOptions.Single(a => a.Name == "VISA");
        editor.SelectedCategory = editor.CategoryOptions.Single(c => c.Name == "Wonen");
        Save(window, "12-quick-add-review");

        await vm.QuickAdd.SaveReviewCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => !vm.QuickAdd.IsReviewOpen);
        Assert.Equal(-123_400, temp.Scalar("SELECT amount_cents FROM transactions;"));
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM payee_rules WHERE payee_key = 'zetel' AND category_id IS NOT NULL;"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Transactions_page_transfer_void_and_undo()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);

        Type(vm.QuickAdd, "Loon 2400");
        await vm.QuickAdd.SubmitCommand.ExecuteAsync(null);
        Type(vm.QuickAdd, "Spotify 17,99 visa");
        await vm.QuickAdd.SubmitCommand.ExecuteAsync(null);

        var transactions = Navigate<TransactionsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 2);

        await transactions.NewTransferCommand.ExecuteAsync(null);
        var editor = transactions.Editor;
        editor.SelectedDestination = editor.AccountOptions.Single(a => a.Name == "VISA");
        editor.AmountText = "312,40";
        editor.Description = "VISA september";
        await transactions.SaveEditorCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 3);

        // Shown once in the all-accounts view, as a card payment, and never in the month's spending.
        var settlement = transactions.Rows.Single(r => r.Transaction.IsTransfer);
        Assert.Equal(PersonalFinanceOS.Application.Ledger.EntryKind.CardSettlement, settlement.Entry.Kind);
        Assert.Equal(Money.FromCents(1_799), transactions.MonthTotals.Expenses);
        Assert.Equal(Money.FromCents(240_000), transactions.MonthTotals.Income);
        Save(window, "13-transactions");

        // Removing a transfer asks once more, then removes both sides.
        await settlement.VoidCommand.ExecuteAsync(null);
        Assert.True(settlement.IsConfirmingVoid);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions WHERE status = 'Voided';"));
        await settlement.VoidCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 2);
        Assert.Equal(2, temp.Scalar("SELECT COUNT(*) FROM transactions WHERE status = 'Voided';"));

        await vm.QuickAdd.UndoCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 3);

        // Filter on the card: its side of the transfer and the purchase.
        transactions.SelectedFilter = transactions.FilterOptions.Single(o => o.Name == "VISA");
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Editing_a_transaction_from_the_list()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        Type(vm.QuickAdd, "Koffie 3,20");
        await vm.QuickAdd.SubmitCommand.ExecuteAsync(null);

        var transactions = Navigate<TransactionsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 1);
        await transactions.Rows[0].EditCommand.ExecuteAsync(null);
        Assert.True(transactions.IsEditorOpen);
        transactions.Editor.AmountText = "3,50";
        transactions.Editor.SelectedType = transactions.Editor.TypeOptions.Single(t => t.Type == TransactionType.Expense);
        await transactions.SaveEditorCommand.ExecuteAsync(null);

        HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 1 && transactions.Rows[0].Transaction.Amount == Money.FromCents(-350));
        Assert.False(transactions.IsEditorOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reconciling_an_account_shows_and_records_the_difference()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        var accounts = Navigate<AccountsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);

        var current = accounts.Rows.Single(r => r.Name == "Zichtrekening");
        Assert.Equal(Loc.T("Accounts_NotReconciled"), current.ReconciliationText);
        await current.ReconcileCommand.ExecuteAsync(null);
        accounts.ReconcileBalanceText = "1.832,35";
        HeadlessSetup.PumpUntil(() => accounts.ReconcilePreview is not null);
        Assert.True(accounts.ReconcileHasDifference);
        accounts.ReconcileReason = "Bankkosten";
        Save(window, "14-reconcile");

        await accounts.SaveReconciliationWithAdjustmentCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => !accounts.IsReconcileOpen && accounts.Rows.Count == 4);
        var after = accounts.Rows.Single(r => r.Name == "Zichtrekening");
        Assert.Equal(Money.FromCents(183_235), after.Balance);
        Assert.False(after.HasReconciliationDifference);
        Assert.Equal(-1_000, temp.Scalar("SELECT amount_cents FROM transactions WHERE type = 'Adjustment';"));

        // A credit card statement is typed as the amount owed.
        var visa = accounts.Rows.Single(r => r.Name == "VISA");
        await visa.ReconcileCommand.ExecuteAsync(null);
        accounts.ReconcileBalanceText = "312,40";
        HeadlessSetup.PumpUntil(() => accounts.ReconcilePreview is not null);
        Assert.False(accounts.ReconcileHasDifference);
        window.Close();
    }

    [AvaloniaFact]
    public void Default_categories_are_created_in_the_interface_language_and_manageable()
    {
        using var temp = new TempDataFolder();
        var (window, vm) = Start(temp);
        var settings = Navigate<SettingsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Count > 0);

        Assert.Contains(settings.CategoryRows, r => r.Name == "Boodschappen");
        Assert.Contains(settings.CategoryRows, r => r.Name == "Loon");

        settings.NewCategoryName = "Kinderopvang";
        settings.SaveCategoryCommand.Execute(null);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Any(r => r.Name == "Kinderopvang"));

        var row = settings.CategoryRows.Single(r => r.Name == "Kinderopvang");
        row.RenameCommand.Execute(null);
        settings.NewCategoryName = "Opvang";
        settings.SaveCategoryCommand.Execute(null);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.Any(r => r.Name == "Opvang"));

        settings.CategoryRows.Single(r => r.Name == "Opvang").ArchiveCommand.Execute(null);
        HeadlessSetup.PumpUntil(() => settings.CategoryRows.All(r => r.Name != "Opvang"));
        Save(window, "15-settings-categories");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ledger_screenshots_in_light_and_dark()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                var name = theme.ToString().ToLowerInvariant();
                var (window, vm) = Start(temp);
                ThemeManager.Apply(theme);
                if (theme == AppTheme.Light)
                {
                    foreach (var line in new[] { "Loon 2400", "Supermarkt 42,80", "Spotify 17,99 visa", "Transfer 600 naar gezamenlijke rekening", "Koffie 3,20 gisteren" })
                    {
                        Type(vm.QuickAdd, line);
                        await vm.QuickAdd.SubmitCommand.ExecuteAsync(null);
                    }
                }

                var transactions = Navigate<TransactionsViewModel>(vm);
                HeadlessSetup.PumpUntil(() => transactions.Rows.Count == 5);
                Type(vm.QuickAdd, "Tankstation 65");
                Save(window, $"{name}-6-transactions");
                vm.QuickAdd.Text = string.Empty;

                var dashboard = Navigate<DashboardViewModel>(vm);
                HeadlessSetup.PumpUntil(() => dashboard.ActiveAccountCount == 4);
                Save(window, $"{name}-7-dashboard-with-ledger");
                window.Close();
            }
        }
        finally
        {
            ThemeManager.Apply(AppTheme.System);
        }
    }
}
