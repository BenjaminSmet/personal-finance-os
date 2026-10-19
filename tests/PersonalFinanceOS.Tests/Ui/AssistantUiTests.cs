using Avalonia.Headless.XUnit;
using PersonalFinanceOS.App;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Infrastructure.Persistence;
using PersonalFinanceOS.Tests.Infrastructure;
using static PersonalFinanceOS.Tests.Ui.LedgerUiTests;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Phase 7 through the real UI: what-if scenarios on the Planning page and the assistant.</summary>
[Collection("UI")]
public class AssistantUiTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>The card paid from the current account, rent and salary, a €500 buffer and a laptop on the wishlist.</summary>
    private static async Task SeedPhase7Async(TempDataFolder temp)
    {
        await SeedAsync(temp);
        var options = FinanceDbContext.OptionsFor(temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var clock = new FixedClock(DateTimeOffset.Now);
        var audit = new AuditLog(db, clock);
        var accountRepository = new AccountRepository(db);
        var categories = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        var ledger = new LedgerService(transactions, accountRepository, categories, audit, new UndoHistory(), clock);
        var recurring = new RecurringService(new RecurringRepository(db), accountRepository, categories, transactions, ledger, audit, clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, recurring, audit, clock);
        var list = await accountRepository.ListAsync(false);
        var current = list.Single(a => a.Name == "Zichtrekening");
        var joint = list.Single(a => a.Name == "Gezamenlijke rekening");
        var visa = list.Single(a => a.Name == "VISA");

        await cards.SaveSettingsAsync(visa.Id, new CardSettings(Money.FromCents(150_000), 28, 5, current.Id));
        await new AppSettingsService(new SettingsStore(db, clock)).SetMinimumBufferAsync(Money.FromCents(50_000));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, null, Money.FromCents(85_000), RecurrenceFrequency.Monthly, Today.AddDays(5)));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, null, Money.FromCents(240_000), RecurrenceFrequency.Monthly, Today.AddDays(15)));
        await recurring.CreateRuleAsync(new RecurringRuleRequest("Gezamenlijke pot", RecurringKind.Transfer, current.Id, joint.Id, null, Money.FromCents(60_000), RecurrenceFrequency.Monthly, Today.AddDays(16)));
        await new WishlistService(new WishlistRepository(db), transactions, ledger, audit, clock).CreateAsync(new WishlistRequest("Laptop", Money.FromCents(70_000)));
    }

    [AvaloniaFact]
    public async Task A_scenario_is_built_on_the_planning_page_and_compared_with_the_baseline()
    {
        using var temp = new TempDataFolder();
        await SeedPhase7Async(temp);
        var (window, vm) = Start(temp);
        var transactions = temp.Scalar("SELECT COUNT(*) FROM transactions;");
        var scenarios = Navigate<ScenariosViewModel>(vm);
        HeadlessSetup.PumpUntil(() => scenarios.Months.Count == 12 && scenarios.Horizons.Count == 3);
        Assert.False(scenarios.HasScenario);
        Assert.All(scenarios.Months, m => Assert.False(m.HasScenario));

        scenarios.OpenNewCommand.Execute(null);
        scenarios.FormName = "Verbouwing duurder";
        await scenarios.SaveNameCommand.ExecuteAsync(null);
        Assert.Null(scenarios.NameError);
        Assert.True(scenarios.HasScenario);
        Assert.True(scenarios.IsChangeFormOpen);

        // An extra renovation bill in three weeks.
        scenarios.ChangeKind = scenarios.ChangeKinds.Single(k => k.Kind == ScenarioChangeKind.OneOff);
        scenarios.ChangeName = "Extra renovatie";
        scenarios.ChangeAmountText = "5.000";
        scenarios.ChangeDate = Today.AddDays(20).ToDateTime(TimeOnly.MinValue);
        await scenarios.SaveChangeCommand.ExecuteAsync(null);
        Assert.Null(scenarios.ChangeError);

        // The laptop from the wishlist, on the VISA: paid when the statement is debited.
        await scenarios.OpenChangeFormCommand.ExecuteAsync(null);
        scenarios.ChangeKind = scenarios.ChangeKinds.Single(k => k.Kind == ScenarioChangeKind.Purchase);
        scenarios.ChangeItem = scenarios.WishlistItems.Single(i => i.Name == "Laptop");
        Assert.Equal("Laptop", scenarios.ChangeName);
        scenarios.ChangePay = scenarios.PayOptions.Single(o => o.Name == "VISA");
        await scenarios.SaveChangeCommand.ExecuteAsync(null);
        Assert.Null(scenarios.ChangeError);

        HeadlessSetup.PumpUntil(() => scenarios.Changes.Count == 2 && scenarios.Months.All(m => m.HasScenario));
        Assert.Contains(scenarios.Changes, c => c.Title.Contains("Extra renovatie", StringComparison.Ordinal));
        Assert.True(scenarios.HasWarnings);
        Assert.NotEmpty(scenarios.Assumptions);
        Assert.Contains(scenarios.Horizons, h => h.DifferenceIsNegative);

        // Per account: the joint account is not touched by these changes.
        scenarios.SelectedProjection = scenarios.Projections.Single(p => p.Name == "Gezamenlijke rekening");
        HeadlessSetup.PumpUntil(() => scenarios.Months.All(m => !m.DifferenceIsNegative));
        scenarios.SelectedProjection = scenarios.Projections[0];
        HeadlessSetup.PumpUntil(() => scenarios.Months.Any(m => m.DifferenceIsNegative));

        window.Height = 2600;
        Save(window, "40-scenarios");

        // Scenarios never post anything.
        Assert.Equal(transactions, temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(2, temp.Scalar("SELECT COUNT(*) FROM scenario_changes;"));

        try
        {
            ThemeManager.Apply(AppTheme.Dark);
            Loc.Instance.SetLanguage(AppLanguage.English);
            HeadlessSetup.PumpUntil(() => scenarios.MonthsTitle.Contains("months", StringComparison.Ordinal));
            Save(window, "dark-14-scenarios-english");
        }
        finally
        {
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
        }

        scenarios.AskDeleteCommand.Execute(null);
        await scenarios.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.False(scenarios.HasScenario);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM scenario_changes;"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_assistant_answers_with_figures_and_saves_a_what_if_only_when_asked()
    {
        using var temp = new TempDataFolder();
        await SeedPhase7Async(temp);
        var (window, vm) = Start(temp);
        var transactions = temp.Scalar("SELECT COUNT(*) FROM transactions;");
        var assistant = Navigate<AssistantViewModel>(vm);
        Assert.Equal(11, assistant.Examples.Count);
        Assert.False(assistant.HasMessages);
        Save(window, "41-assistant-start");

        // Typed and sent like the Enter key does.
        assistant.Question = "Kan ik €300 uitgeven?";
        await assistant.AskCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, assistant.Question);
        var afford = Assert.IsType<AnswerMessageViewModel>(assistant.Messages[^1]);
        Assert.Equal(AssistantIntent.CanAfford, afford.Answer.Intent);
        Assert.True(afford.HasFigures);
        Assert.True(afford.HasAssumptions);
        Assert.DoesNotContain("Ask_", afford.Headline, StringComparison.Ordinal);

        // A missing amount: one question, answered by a short reply.
        await assistant.AskTextAsync("Kan ik dit betalen?");
        var question = Assert.IsType<AnswerMessageViewModel>(assistant.Messages[^1]);
        Assert.Equal("Vraag", question.KindText);
        await question.Suggestions.Single(s => s.Text == "Laptop").Command.ExecuteAsync(null);
        var laptop = Assert.IsType<AnswerMessageViewModel>(assistant.Messages[^1]);
        Assert.Contains("Laptop", laptop.Headline, StringComparison.Ordinal);

        await assistant.AskTextAsync("Wat als de renovatie €5.000 meer kost?");
        var whatIf = Assert.IsType<AnswerMessageViewModel>(assistant.Messages[^1]);
        Assert.Equal(AssistantIntent.WhatIf, whatIf.Answer.Intent);
        Assert.Equal(0, temp.Scalar("SELECT COUNT(*) FROM scenarios;"));
        window.Height = 2400;
        Save(window, "42-assistant");

        // Saving is the user's click; then the button opens the saved scenario.
        await whatIf.Actions.Single(a => a.IsPrimary).Command.ExecuteAsync(null);
        Assert.NotNull(whatIf.SavedScenarioId);
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM scenarios;"));
        Assert.NotNull(whatIf.SavedText);
        await whatIf.Actions.Single(a => a.IsPrimary).Command.ExecuteAsync(null);
        Assert.Equal(1, temp.Scalar("SELECT COUNT(*) FROM scenarios;"));
        var forecasts = Assert.IsType<ForecastsViewModel>(vm.CurrentPage);
        var scenarios = Assert.IsType<ScenariosViewModel>(forecasts.Current);
        HeadlessSetup.PumpUntil(() => scenarios.SelectedScenario?.Id == whatIf.SavedScenarioId && scenarios.Changes.Count == 1);
        window.Height = 900;

        // The assistant never posts anything.
        Assert.Equal(transactions, temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        assistant = Navigate<AssistantViewModel>(vm);
        try
        {
            ThemeManager.Apply(AppTheme.Dark);
            Loc.Instance.SetLanguage(AppLanguage.English);
            assistant.ClearCommand.Execute(null);
            await assistant.AskTextAsync("Should I use VISA or wait for the laptop?");
            var card = Assert.IsType<AnswerMessageViewModel>(assistant.Messages[^1]);
            Assert.Equal(AssistantIntent.CardOrWait, card.Answer.Intent);
            window.Height = 1500;
            Save(window, "dark-15-assistant-english");
        }
        finally
        {
            Loc.Instance.SetLanguage(AppLanguage.Dutch);
            ThemeManager.Apply(AppTheme.System);
            window.Close();
        }
    }
}
