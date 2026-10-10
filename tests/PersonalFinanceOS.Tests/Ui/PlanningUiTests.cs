using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 4 through the real UI: card settings, recurring items, confirming, safe-to-spend.</summary>
[Collection("UI")]
public class PlanningUiTests
{
    private static async Task AddRuleAsync(PlanningViewModel planning, string name, RecurringKind kind, string account, string amount, DateOnly start, string? to = null)
    {
        planning.OpenRuleFormCommand.Execute(null);
        planning.NewName = name;
        planning.NewKind = planning.KindOptions.Single(k => k.Kind == kind);
        planning.NewAccount = planning.AccountOptions.Single(a => a.Name == account);
        if (to is not null)
        {
            planning.NewToAccount = planning.AccountOptions.Single(a => a.Name == to);
        }

        planning.NewAmountText = amount;
        planning.NewStartDate = start.ToDateTime(TimeOnly.MinValue);
        await planning.SaveRuleCommand.ExecuteAsync(null);
        Assert.Null(planning.RuleError);
    }

    private static async Task<PlanningViewModel> SetUpAsync(MainWindowViewModel vm)
    {
        // The card's debit day and paying account are set on the Accounts page.
        var accounts = Navigate<AccountsViewModel>(vm);
        HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);
        var visa = accounts.Rows.Single(r => r.Name == "VISA");
        await visa.EditCardCommand.ExecuteAsync(null);
        accounts.CardSettlementDayText = "5";
        accounts.CardSettlementAccount = accounts.SettlementOptions.Single(o => o.Name == "Zichtrekening");
        await accounts.SaveCardCommand.ExecuteAsync(null);
        Assert.Null(accounts.CardError);

        var planning = Navigate<PlanningViewModel>(vm);
        HeadlessSetup.PumpUntil(() => planning.AccountOptions.Count == 4);
        var today = DateOnly.FromDateTime(DateTime.Today);
        await AddRuleAsync(planning, "Loon", RecurringKind.Income, "Zichtrekening", "2.400", today.AddDays(12));
        await AddRuleAsync(planning, "Huur", RecurringKind.Expense, "Zichtrekening", "850", today.AddDays(-2));
        await AddRuleAsync(planning, "Sparen", RecurringKind.Transfer, "Zichtrekening", "200", today.AddDays(3), "Spaarrekening 1");
        await AddRuleAsync(planning, "Streaming", RecurringKind.Expense, "VISA", "13,99", today.AddDays(5));
        // Huur also comes back within the 30 days shown, so five open items.
        HeadlessSetup.PumpUntil(() => planning.Rules.Count == 4 && planning.Planned.Count == 5);
        return planning;
    }

    [AvaloniaFact]
    public async Task Planned_items_are_confirmed_and_safe_to_spend_follows()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        var planning = await SetUpAsync(vm);

        Assert.True(planning.CanCalculate);
        Assert.Contains(planning.SafeLines, l => l.Label.StartsWith("Huur", StringComparison.Ordinal));
        Assert.Contains(planning.SafeLines, l => l.Label.StartsWith("Sparen", StringComparison.Ordinal));
        Assert.DoesNotContain(planning.SafeLines, l => l.Label.StartsWith("Loon", StringComparison.Ordinal)); // income is never counted early
        Assert.Contains("Loon", planning.SafeUntilText, StringComparison.Ordinal);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM transactions;")); // plans are not postings

        var rent = planning.Planned.Single(p => p.Name == "Huur" && p.IsOverdue);
        Assert.True(rent.IsOverdue);
        window.Height = 2300;
        Save(window, "19-planning");

        rent.EditAmountText = "865";
        await rent.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(rent.Error);
        Assert.Equal("Huur bevestigd en geregistreerd.", planning.StatusMessage);
        Assert.Equal(-86_500, temp.Scalar("SELECT amount_cents FROM transactions WHERE description = 'Huur';"));
        HeadlessSetup.PumpUntil(() => planning.Planned.Count == 4);
        Assert.DoesNotContain(planning.Planned, p => p.IsOverdue);

        var dashboard = Navigate<DashboardViewModel>(vm);
        HeadlessSetup.PumpUntil(() => dashboard.HasSafeToSpend);
        Assert.Null(dashboard.AttentionText); // nothing overdue any more
        window.Height = 900;
        Save(window, "20-dashboard-safe-to-spend");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Card_settings_screenshots_and_planning_in_dark_and_english()
    {
        using var temp = new TempDataFolder();
        await SeedAsync(temp);
        var (window, vm) = Start(temp);
        try
        {
            var accounts = Navigate<AccountsViewModel>(vm);
            HeadlessSetup.PumpUntil(() => accounts.Rows.Count == 4);
            accounts.OpenFormCommand.Execute(null);
            accounts.NewName = "Mastercard";
            accounts.NewType = accounts.TypeOptions.Single(o => o.Type == global::PersonalFinanceOS.Domain.Accounts.AccountType.CreditCard);
            accounts.CardLimitText = "2500";
            accounts.CardStartDayText = "28";
            HeadlessSetup.Pump();
            window.Height = 1300;
            Save(window, "21-new-card-settings");
            accounts.CancelFormCommand.Execute(null);

            var planning = await SetUpAsync(vm);
            ThemeManager.Apply(AppTheme.Dark);
            Loc.Instance.SetLanguage(AppLanguage.English);
            HeadlessSetup.PumpUntil(() => planning.SafeLines.Count > 0 && planning.SafeLines[0].Label.StartsWith("Balance", StringComparison.Ordinal));
            window.Height = 2300;
            Save(window, "dark-10-planning-english");
        }
        finally
        {
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
            window.Close();
        }
    }
}
