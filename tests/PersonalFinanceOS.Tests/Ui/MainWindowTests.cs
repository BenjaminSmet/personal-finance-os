using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.App.Views;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>
/// Starts the real application UI headlessly against a temporary data folder.
/// Also saves screenshots (bin/.../screenshots) so the UI can be reviewed without Windows.
/// </summary>
[Collection("UI")]
public class MainWindowTests
{
    private static string ScreenshotDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("PFOS_SCREENSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static Window Start(TempDataFolder temp)
    {
        var app = (PersonalFinanceOS.App.App)Avalonia.Application.Current!;
        var window = app.CreateStartWindow(temp.Paths, _ => { });
        window.Width = 1280;
        window.Height = 820;
        window.Show();
        return window;
    }

    private static void Save(Window window, string name)
    {
        HeadlessSetup.Pump(250);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDirectory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static async Task SeedSampleAccountsAsync(TempDataFolder temp)
    {
        Assert.True(temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var service = new AccountService(new AccountRepository(db), new AuditLog(db, clock), clock);
        var date = new DateOnly(2026, 10, 1);
        await service.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, date, Money.FromCents(184_235), Share.Full));
        await service.CreateAsync(new CreateAccountRequest("Gezamenlijke rekening", AccountType.JointCurrent, date, Money.FromCents(240_000), new Share(5000)));
        await service.CreateAsync(new CreateAccountRequest("Spaarrekening 1", AccountType.Savings, date, Money.FromCents(815_000), Share.Full));
        await service.CreateAsync(new CreateAccountRequest("VISA", AccountType.CreditCard, date, Money.FromCents(-31_240), Share.Full));
    }

    [AvaloniaFact]
    public void Fresh_start_shows_dashboard_with_all_sections()
    {
        using var temp = new TempDataFolder();
        var window = Start(temp);

        var main = Assert.IsType<MainWindow>(window);
        var vm = Assert.IsType<MainWindowViewModel>(main.DataContext);
        Assert.Equal(11, vm.NavItems.Count); // the ten sections of spec §22 plus Import (D-017)
        Assert.IsType<DashboardViewModel>(vm.CurrentPage);
        Assert.True(File.Exists(temp.Paths.DatabaseFile));

        Save(window, "01-dashboard-empty");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Accounts_page_lists_accounts_and_dashboard_totals_liquid_only()
    {
        using var temp = new TempDataFolder();
        await SeedSampleAccountsAsync(temp);
        var window = Start(temp);
        var vm = (MainWindowViewModel)window.DataContext!;
        var dashboard = (DashboardViewModel)vm.CurrentPage;

        HeadlessSetup.PumpUntil(() => dashboard.ActiveAccountCount == 4);

        // Liquid = personal current + full joint balance. Savings and VISA debt are not spendable cash.
        Assert.Equal(Money.FromCents(184_235 + 240_000), dashboard.LiquidTotal);
        Save(window, "02-dashboard-with-accounts");

        vm.SelectedItem = vm.NavItems.Single(n => n.Page is AccountsViewModel);
        var accounts = (AccountsViewModel)vm.CurrentPage;
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);
        Save(window, "03-accounts");

        accounts.OpenFormCommand.Execute(null);
        accounts.NewType = accounts.TypeOptions.Single(o => o.Type == AccountType.CreditCard);
        accounts.NewBalanceText = "1.234";
        HeadlessSetup.Pump();
        Assert.NotNull(accounts.AmountHint);
        Save(window, "04-add-account-form");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Adding_an_account_through_the_form_saves_it()
    {
        using var temp = new TempDataFolder();
        var window = Start(temp);
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.SelectedItem = vm.NavItems.Single(n => n.Page is AccountsViewModel);
        var accounts = (AccountsViewModel)vm.CurrentPage;

        accounts.OpenFormCommand.Execute(null);
        accounts.NewName = "VISA";
        accounts.NewType = accounts.TypeOptions.Single(o => o.Type == AccountType.CreditCard);
        accounts.NewBalanceText = "312,40";
        await accounts.SaveCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 1);

        var row = accounts.Rows[0];
        Assert.Equal(Money.FromCents(-31_240), row.Account.OpeningBalance); // typed as owed, stored as debt
        Assert.Null(accounts.FormError);
        Assert.False(accounts.IsFormOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Invalid_amount_shows_a_translated_error_and_saves_nothing()
    {
        using var temp = new TempDataFolder();
        var window = Start(temp);
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.SelectedItem = vm.NavItems.Single(n => n.Page is AccountsViewModel);
        var accounts = (AccountsViewModel)vm.CurrentPage;

        accounts.OpenFormCommand.Execute(null);
        accounts.NewName = "Test";
        accounts.NewBalanceText = "twaalf";
        await accounts.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Loc.Error("amount.invalid"), accounts.FormError);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM accounts;"));
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_language_changes_texts_and_is_remembered()
    {
        using var temp = new TempDataFolder();
        var window = Start(temp);
        var vm = (MainWindowViewModel)window.DataContext!;
        Assert.Equal("Rekeningen", vm.NavItems[2].Title);

        vm.SelectedItem = vm.NavItems.Single(n => n.Page is SettingsViewModel);
        var settings = (SettingsViewModel)vm.CurrentPage;
        settings.SelectedLanguage = settings.Languages.Single(l => l.Language == AppLanguage.English);
        HeadlessSetup.PumpUntil(() => Loc.Instance.Language == AppLanguage.English);

        Assert.Equal("Accounts", vm.NavItems[2].Title);
        HeadlessSetup.Pump();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        // Page content (not only the menu) must switch: section headings and the encryption notice.
        Assert.Contains("Language", texts);
        Assert.Contains("Your data", texts);
        Assert.DoesNotContain("Jouw gegevens", texts);
        Assert.DoesNotContain("Taal", texts);
        Save(window, "05-settings-english");
        window.Close();

        // Restart: the language comes back from the database.
        Loc.Instance.SetLanguage(AppLanguage.Dutch);
        var again = Start(temp);
        Assert.Equal(AppLanguage.English, Loc.Instance.Language);
        again.Close();
        Loc.Instance.SetLanguage(AppLanguage.Dutch);
    }

    [AvaloniaFact]
    public void Theme_switch_applies_live_and_is_remembered()
    {
        using var temp = new TempDataFolder();
        try
        {
            var window = Start(temp);
            var vm = (MainWindowViewModel)window.DataContext!;
            vm.SelectedItem = vm.NavItems.Single(n => n.Page is SettingsViewModel);
            var settings = (SettingsViewModel)vm.CurrentPage;

            settings.SelectedTheme = settings.Themes.Single(t => t.Theme == AppTheme.Dark);
            HeadlessSetup.PumpUntil(() => Avalonia.Application.Current!.ActualThemeVariant == ThemeVariant.Dark);
            window.Close();

            // Restart: dark mode comes back from the database.
            ThemeManager.Apply(AppTheme.System);
            var again = Start(temp);
            Assert.Equal(AppTheme.Dark, ThemeManager.Current);
            Assert.Equal(ThemeVariant.Dark, Avalonia.Application.Current!.ActualThemeVariant);
            again.Close();
        }
        finally
        {
            ThemeManager.Apply(AppTheme.System);
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
        }
    }

    [AvaloniaFact]
    public async Task Show_archived_switch_reveals_archived_accounts()
    {
        using var temp = new TempDataFolder();
        await SeedSampleAccountsAsync(temp);
        var window = Start(temp);
        var vm = (MainWindowViewModel)window.DataContext!;
        vm.SelectedItem = vm.NavItems.Single(n => n.Page is AccountsViewModel);
        var accounts = (AccountsViewModel)vm.CurrentPage;
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);

        await accounts.Rows[0].ArchiveCommand.ExecuteAsync(null);
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 3);

        var toggle = window.GetVisualDescendants().OfType<ToggleSwitch>().Single();
        Assert.False(toggle.IsChecked);
        toggle.IsChecked = true; // same as clicking the switch
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);
        Assert.True(accounts.ShowArchived);
        Save(window, "light-6-switch-on");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Screenshot_gallery_in_light_and_dark()
    {
        using var temp = new TempDataFolder();
        await SeedSampleAccountsAsync(temp);
        try
        {
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                var name = theme.ToString().ToLowerInvariant();
                var window = Start(temp);
                ThemeManager.Apply(theme);
                var vm = (MainWindowViewModel)window.DataContext!;
                var dashboard = (DashboardViewModel)vm.CurrentPage;
                HeadlessSetup.PumpUntil(() => dashboard.ActiveAccountCount == 4);
                Save(window, $"{name}-1-dashboard");

                vm.SelectedItem = vm.NavItems.Single(n => n.Page is AccountsViewModel);
                var accounts = (AccountsViewModel)vm.CurrentPage;
                HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);
                Save(window, $"{name}-2-accounts");

                accounts.OpenFormCommand.Execute(null);
                accounts.NewName = "Cash";
                accounts.NewType = accounts.TypeOptions.Single(o => o.Type == AccountType.Cash);
                accounts.NewBalanceText = "1.250";
                Save(window, $"{name}-3-add-account");
                accounts.CancelFormCommand.Execute(null);

                vm.SelectedItem = vm.NavItems.Single(n => n.Page is SettingsViewModel);
                Save(window, $"{name}-4-settings");

                vm.SelectedItem = vm.NavItems.First(n => n.Page is PlaceholderViewModel);
                Save(window, $"{name}-5-coming-soon");
                window.Close();
            }
        }
        finally
        {
            ThemeManager.Apply(AppTheme.System);
        }
    }

    [AvaloniaFact]
    public void Missing_database_shows_recovery_screen_instead_of_an_empty_app()
    {
        using var temp = new TempDataFolder();
        temp.Initializer().Initialize();
        File.Delete(temp.Paths.DatabaseFile);

        var window = Start(temp);

        Assert.IsType<StartupProblemWindow>(window);
        Assert.False(File.Exists(temp.Paths.DatabaseFile));
        Save(window, "06-recovery-missing-database");
        window.Close();
        Loc.Instance.SetLanguage(AppLanguage.Dutch);
    }
}
