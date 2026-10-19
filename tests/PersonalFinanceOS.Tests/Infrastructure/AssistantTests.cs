using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Assistant;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// Phase 7 against a real migrated database: the rule-based assistant (spec §15). The fixture: history since 1 August,
/// €2,000 on the current account today (10 October), €500 buffer, rent €900 on the 15th, salary €2,400 on the 25th, a
/// €400 VISA purchase debited on 5 November, savings, a €400 grocery budget with €270 spent and a €700 laptop wish.
/// What the history shows (D-039): a €15 subscription on the 12th that nobody planned, and usual day-to-day spending of
/// €350 a month (groceries €300 and €400). October already has €670 of it (€270 groceries, €400 on the VISA), so the
/// rest of October is counted at the usual pace: €350 × 21/31 = €237.10, €158.07 of it until the salary.
/// </summary>
public sealed class AssistantTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 8, 1);
    private static readonly DateOnly Today = new(2026, 10, 10);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly AppSettingsService _settings;
    private readonly CategoryService _categories;
    private readonly BudgetService _budgets;
    private readonly WishlistService _wishlist;
    private readonly ScenarioService _scenarios;
    private readonly AssistantService _assistant;

    public AssistantTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var budgetRepository = new BudgetRepository(db);
        var transactions = new TransactionRepository(db);
        var wishlistRepository = new WishlistRepository(db);
        _settings = new AppSettingsService(new SettingsStore(db, _clock));
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _categories = new CategoryService(categoryRepository, budgetRepository, new SettingsStore(db, _clock), audit, _clock);
        _budgets = new BudgetService(budgetRepository, categoryRepository, audit, _clock);
        _recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, _recurring, audit, _clock);
        var spending = new SpendingProfileService(accountRepository, categoryRepository, transactions, new CreditCardRepository(db), _recurring, _clock);
        var forecast = new ForecastService(accountRepository, _ledger, _recurring, cards, _settings, spending, _clock);
        var affordability = new AffordabilityService(forecast, cards);
        var analysis = new AnalysisService(accountRepository, categoryRepository, transactions, budgetRepository, new OwnershipRepository(db), _recurring, _clock);
        _wishlist = new WishlistService(wishlistRepository, transactions, _ledger, audit, _clock);
        _scenarios = new ScenarioService(new ScenarioRepository(db), accountRepository, wishlistRepository, _recurring, forecast, cards, audit, _clock);
        _assistant = new AssistantService(accountRepository, categoryRepository, _wishlist, _recurring, forecast, affordability, analysis, _scenarios, spending, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static Money Euro(long euros) => Money.FromCents(euros * 100);

    private sealed record Fixture(Account Current, Account Visa, Account Savings, Category Groceries, Category Subscriptions);

    private async Task<Fixture> SetUpAsync()
    {
        var current = await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Money.Zero, Share.Full));
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Euro(1_500), 28, 5, current.Id)));
        var savings = await _accounts.CreateAsync(new CreateAccountRequest("Spaarrekening", AccountType.Savings, Opening, Euro(5_000), Share.Full));
        await _settings.SetMinimumBufferAsync(Euro(500));

        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var subscriptions = await _categories.CreateAsync("Abonnementen", CategoryKind.Expense);
        var housing = await _categories.CreateAsync("Wonen", CategoryKind.Expense);
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);

        // August and September: salary, rent, groceries (€300, then €400) and a €15 subscription. October so far: €270 groceries.
        foreach (var (month, food) in new[] { (8, 300L), (9, 400L) })
        {
            await Record(current, new(2026, month, 1), 2_400, TransactionType.Income, salary);
            await Record(current, new(2026, month, 3), 900, TransactionType.Expense, housing);
            await Record(current, new(2026, month, 8), food, TransactionType.Expense, groceries);
            await Record(current, new(2026, month, 12), 15, TransactionType.Expense, subscriptions);
        }

        await Record(current, new(2026, 10, 4), 270, TransactionType.Expense, groceries);
        await _budgets.SetAsync(groceries.Id, new(2026, 10, 1), Euro(400));

        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, housing.Id, Euro(900), RecurrenceFrequency.Monthly, new(2026, 10, 15)));
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, salary.Id, Euro(2_400), RecurrenceFrequency.Monthly, new(2026, 10, 25)));
        await _ledger.RecordAsync(new TransactionRequest(visa.Id, new(2026, 10, 2), Euro(400), TransactionType.Expense, "Winkel"));
        await _wishlist.CreateAsync(new WishlistRequest("Laptop", Euro(700)));
        Assert.Equal(Euro(2_000), await _ledger.GetBalanceAsync(current.Id));
        return new Fixture(current, visa, savings, groceries, subscriptions);
    }

    private Task<Transaction> Record(Account account, DateOnly date, long euros, TransactionType type, Category category) =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, date, Euro(euros), type, category.Name, category.Id));

    private static bool Has(IEnumerable<AnswerText> texts, string key) => texts.Any(t => t.Key == key);

    private static object? Figure(AssistantAnswer answer, string labelKey) => answer.Figures.FirstOrDefault(f => f.Label.Key == labelKey)?.Value;

    // ===== Can I afford this? =====

    [Fact]
    public async Task Can_I_afford_answers_prudent_or_technically_with_the_figures_it_used()
    {
        await SetUpAsync();

        var yes = await _assistant.AskAsync("Kan ik €400 uitgeven?");
        Assert.Equal((AssistantIntent.CanAfford, AnswerKind.Direct, "Ask_Afford_Prudent"), (yes.Intent, yes.Kind, yes.Headline.Key));
        Assert.Equal(Euro(2_000), Figure(yes, "Check_CashNow"));
        Assert.Equal(Euro(1_600), Figure(yes, "Check_CashAfter"));

        // €500 would be fine on the planned bills alone, but not with the usual spending and the subscription until payday.
        var usual = await _assistant.AskAsync("Kan ik €500 uitgeven?");
        Assert.Equal((AnswerKind.Qualified, "Ask_Afford_Technical"), (usual.Kind, usual.Headline.Key));
        Assert.Equal(Money.FromCents(42_693), usual.Headline.Args[1]);

        // Expected salary is an assumption, never a certainty, and savings are not spendable.
        Assert.True(Has(yes.Assumptions, "Ask_Assume_Income"));
        Assert.True(Has(yes.Assumptions, "Ask_Assume_NotSpendable"));
        Assert.Contains(yes.Considered, t => t.Key == "Ask_Event_Out" && t.Args[0] is "Huur");
        Assert.Contains(yes.Considered, t => t.Key == "Ask_Event_Income" && t.Args[0] is "Loon");
        Assert.Contains(yes.Considered, t => t.Key == "Ask_Event_Out" && t.Args[0] is AnswerText { Key: "Ask_Name_Detected" } n && (string)n.Args[0] == "Abonnementen");
        Assert.Equal("Ask_Event_Usual", yes.Considered[0].Key);
        Assert.True(Has(yes.Assumptions, "Ask_Assume_Usual"));
        Assert.True(Has(yes.Assumptions, "Ask_Assume_Detected"));
        Assert.Equal([AssistantAction.OpenWishlist], yes.Actions);

        var tight = await _assistant.AskAsync("Can I afford €700?");
        Assert.Equal((AnswerKind.Qualified, "Ask_Afford_Technical"), (tight.Kind, tight.Headline.Key));
        Assert.Equal(("Ask_Earliest", (object)new DateOnly(2026, 10, 25)), (tight.Explanation!.Key, tight.Explanation.Args[0]));

        var no = await _assistant.AskAsync("Kan ik €2.500 betalen?");
        Assert.Equal("Ask_Afford_No", no.Headline.Key);
    }

    [Fact]
    public async Task A_missing_amount_gets_one_focused_question_and_the_reply_completes_it()
    {
        await SetUpAsync();

        var question = await _assistant.AskAsync("Can I afford this?");
        Assert.Equal((AnswerKind.Question, "Ask_Q_Amount"), (question.Kind, question.Headline.Key));
        Assert.Equal(MissingValue.Amount, question.Pending!.Missing);
        Assert.Equal(["Laptop"], question.Suggestions);
        Assert.Empty(question.Figures);

        var answer = await _assistant.AskAsync("€400", question.Pending);
        Assert.Equal((AssistantIntent.CanAfford, "Ask_Afford_Prudent"), (answer.Intent, answer.Headline.Key));

        // Clicking the suggested wishlist item works as well.
        var item = await _assistant.AskAsync("Laptop", question.Pending);
        Assert.Equal(("Ask_What_Item", (object)"Laptop"), (((AnswerText)item.Headline.Args[0]).Key, ((AnswerText)item.Headline.Args[0]).Args[0]));

        // A new question is a new question, not a reply.
        var other = await _assistant.AskAsync("Welke betalingen komen eraan?", question.Pending);
        Assert.Equal(AssistantIntent.Upcoming, other.Intent);
    }

    // ===== Can I spend another €100 on groceries this month? =====

    [Fact]
    public async Task Category_room_uses_fixed_costs_and_usual_spending_never_the_budget()
    {
        var f = await SetUpAsync();

        var yes = await _assistant.AskAsync("Kan ik deze maand nog €50 uitgeven aan boodschappen?");
        Assert.Equal((AssistantIntent.CategoryRoom, AnswerKind.Direct, "Ask_Room_Yes"), (yes.Intent, yes.Kind, yes.Headline.Key));
        Assert.Equal(Euro(270), Figure(yes, "Ask_F_SpentThisMonth"));
        Assert.Equal(Euro(350), Figure(yes, "Ask_F_UsualCategory"));

        // €2,000 − €900 rent − €15 subscription − €158.07 usual spending − €500 buffer before the salary.
        Assert.Equal(Money.FromCents(42_693), Figure(yes, "Ask_F_SafeUntil"));
        Assert.DoesNotContain(yes.Figures, x => x.Label.Key.Contains("Budget", StringComparison.Ordinal));
        Assert.Equal([AssistantAction.OpenForecasts], yes.Actions);

        // More than usual on groceries still fits, and the answer says it is more than usual.
        var more = await _assistant.AskAsync("Can I spend another €200 on groceries this month?");
        Assert.Equal((AnswerKind.Qualified, "Ask_Room_MoreThanUsual"), (more.Kind, more.Headline.Key));
        Assert.Equal(Euro(350), more.Headline.Args[4]);

        var tooMuch = await _assistant.AskAsync("Kan ik nog €700 uitgeven aan boodschappen?");
        Assert.Equal("Ask_Room_No", tooMuch.Headline.Key);

        // The budget plays no part: a tiny budget changes nothing.
        await _budgets.SetAsync(f.Groceries.Id, new(2026, 10, 1), Euro(1));
        var again = await _assistant.AskAsync("Kan ik deze maand nog €50 uitgeven aan boodschappen?");
        Assert.Equal((yes.Kind, yes.Headline.Key), (again.Kind, again.Headline.Key));
        Assert.Equal(yes.Headline.Args, again.Headline.Args);
    }

    // ===== How much can I safely spend before payday? =====

    [Fact]
    public async Task Safe_to_spend_before_payday_shows_its_parts()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("How much can I safely spend before payday?");

        Assert.Equal((AssistantIntent.SafeToSpend, "Ask_Safe"), (answer.Intent, answer.Headline.Key));
        Assert.Equal(Money.FromCents(42_693), answer.Headline.Args[0]);
        Assert.Equal(Euro(2_000), Figure(answer, "Ask_F_Balance"));
        Assert.Equal(Euro(-915), Figure(answer, "Ask_F_PlannedOut"));
        Assert.Equal(Money.FromCents(-15_807), Figure(answer, "Ask_F_Usual"));

        // "Can I still spend something extra?" without an amount is the same question.
        var extra = await _assistant.AskAsync("Kan ik deze maand nog iets extra uitgeven?");
        Assert.Equal((AssistantIntent.SafeToSpend, answer.Headline.Key), (extra.Intent, extra.Headline.Key));
        Assert.Equal(answer.Headline.Args, extra.Headline.Args);
        Assert.Equal("Ask_Explain_Safe", answer.Explanation!.Key);
        Assert.Equal([AssistantAction.OpenForecasts], answer.Actions);
    }

    // ===== What payments are coming up? =====

    [Fact]
    public async Task Upcoming_payments_list_bills_and_card_statements_with_the_paying_account()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("Welke betalingen komen eraan?");

        Assert.Equal("Ask_Upcoming", answer.Headline.Key);
        Assert.Equal([3, Euro(1_315), AssistantService.UpcomingDays], answer.Headline.Args);
        Assert.Contains(answer.Considered, t => t.Key == "Ask_Event_OutFrom" && t.Args[0] is AnswerText { Key: "Ask_Name_Detected" } && (DateOnly)t.Args[2] == new DateOnly(2026, 10, 12));
        Assert.Contains(answer.Considered, t => t.Key == "Ask_Event_OutFrom" && t.Args[0] is "Huur" && (string)t.Args[3] == "Zichtrekening");
        Assert.Contains(answer.Considered, t => t.Key == "Ask_Event_CardFrom" && (string)t.Args[0] == "VISA" && (DateOnly)t.Args[2] == new DateOnly(2026, 11, 5));
        Assert.Equal(Euro(2_400), Figure(answer, "Ask_F_ExpectedIn"));
    }

    // ===== Why will my balance be lower next month? =====

    [Fact]
    public async Task Why_lower_names_the_rare_and_large_payments_of_next_month()
    {
        var f = await SetUpAsync();
        var higher = await _assistant.AskAsync("Waarom is mijn saldo volgende maand lager?");
        Assert.Equal("Ask_Lower_No", higher.Headline.Key);

        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Autoverzekering", RecurringKind.Expense, f.Current.Id, null, null, Euro(1_200), RecurrenceFrequency.Yearly, new(2026, 11, 20)));
        var answer = await _assistant.AskAsync("Why will my balance be lower next month?");

        // End of October 3,247.90 (3,500 − 15 subscription − 237.10 usual spending); November − 400 VISA − 900 rent
        // − 1,200 insurance − 15 subscription − 350 usual spending + 2,400 salary.
        Assert.Equal((AssistantIntent.WhyLower, AnswerKind.Direct, "Ask_Lower_Yes"), (answer.Intent, answer.Kind, answer.Headline.Key));
        Assert.Equal([Money.FromCents(324_790), Money.FromCents(278_290), Euro(2_865), Euro(2_400), new MonthRef(new(2026, 11, 1))], answer.Headline.Args);
        Assert.Equal(("Ask_Event_UsualMonth", (object)Euro(350)), (answer.Considered[0].Key, answer.Considered[0].Args[0]));
        Assert.Equal("Ask_Event_Rare", answer.Considered[1].Key);
        Assert.Equal("Autoverzekering", answer.Considered[1].Args[0]);
    }

    // ===== What if renovation costs increase by €5,000? =====

    [Fact]
    public async Task What_if_is_calculated_as_an_unsaved_scenario_and_saved_only_on_request()
    {
        var f = await SetUpAsync();
        var balances = await _ledger.GetBalancesAsync();

        var answer = await _assistant.AskAsync("Wat als de renovatie €5.000 meer kost?");
        Assert.Equal((AssistantIntent.WhatIf, AnswerKind.Qualified, "Ask_WhatIf_BelowZero"), (answer.Intent, answer.Kind, answer.Headline.Key));
        Assert.Equal([Today, Euro(-3_000)], answer.Headline.Args);
        Assert.Equal([AssistantAction.SaveScenario, AssistantAction.OpenScenarios], answer.Actions);
        Assert.Equal("Wat als de renovatie €5.000 meer kost", answer.ScenarioName);
        var request = Assert.Single(answer.Scenario!);
        Assert.Equal((ScenarioChangeKind.OneOff, Euro(5_000), f.Current.Id, false), (request.Kind, request.Amount, request.AccountId, request.IsIncome));
        Assert.True(Has(answer.Assumptions, "Ask_Assume_Today"));
        Assert.Empty(await _scenarios.ListAsync());

        // Saving is a separate step the user takes.
        var saved = await _scenarios.CreateAsync(answer.ScenarioName!, null, answer.Scenario);
        Assert.Single(await _scenarios.ListChangesAsync(saved.Id));
        Assert.Equal(balances, await _ledger.GetBalancesAsync());
    }

    [Fact]
    public async Task What_if_salary_drops_changes_the_planned_salary()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("What if my salary drops by 200 euro per month?");

        Assert.Equal((AnswerKind.Direct, "Ask_WhatIf_Ok"), (answer.Kind, answer.Headline.Key));
        var request = Assert.Single(answer.Scenario!);
        Assert.Equal((ScenarioChangeKind.ChangePlanned, Euro(2_200)), (request.Kind, request.Amount));
        Assert.Contains(answer.Assumptions, t => t.Key == "Ask_Change_Planned" && (string)t.Args[0] == "Loon");

        var missing = await _assistant.AskAsync("Wat als de huur stijgt?");
        Assert.Equal((AnswerKind.Question, "Ask_Q_WhatIfAmount"), (missing.Kind, missing.Headline.Key));
        var rent = await _assistant.AskAsync("€50", missing.Pending);
        Assert.Equal((ScenarioChangeKind.ChangePlanned, Euro(950)), (rent.Scenario![0].Kind, rent.Scenario[0].Amount));
        Assert.Equal("Wat als de huur stijgt", rent.ScenarioName);
    }

    // ===== When can I afford a wishlist item? =====

    [Fact]
    public async Task When_can_I_afford_gives_the_earliest_prudent_date()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("Wanneer kan ik de laptop kopen?");

        Assert.Equal((AssistantIntent.WhenAfford, "Ask_When_Date"), (answer.Intent, answer.Headline.Key));
        Assert.Equal(new DateOnly(2026, 10, 25), answer.Headline.Args[1]);
        Assert.True(Has(answer.Assumptions, "Ask_Assume_NothingElse"));

        var now = await _assistant.AskAsync("When can I afford €300?");
        Assert.Equal("Ask_When_Now", now.Headline.Key);
    }

    // ===== How much did I spend on subscriptions? =====

    [Fact]
    public async Task Spent_on_a_category_over_a_period_with_history_caveats()
    {
        await SetUpAsync();

        var two = await _assistant.AskAsync("Hoeveel heb ik de afgelopen 2 maanden uitgegeven aan abonnementen?");
        Assert.Equal((AssistantIntent.SpentOn, AnswerKind.Direct, "Ask_Spent"), (two.Intent, two.Kind, two.Headline.Key));
        Assert.Equal(Euro(30), two.Headline.Args[0]);
        Assert.Equal(Euro(15), Figure(two, "Ask_F_Average"));

        var year = await _assistant.AskAsync("How much did I spend on subscriptions this year?");
        Assert.Equal(AnswerKind.Qualified, year.Kind);
        Assert.True(Has(year.Assumptions, "Ask_Assume_History"));

        var groceries = await _assistant.AskAsync("How much did I spend on groceries?");
        Assert.Equal(Euro(270), groceries.Headline.Args[0]);
        Assert.Equal(Euro(350), Figure(groceries, "Ask_F_UsualCategory"));
        Assert.DoesNotContain(groceries.Figures, x => x.Label.Key.Contains("Budget", StringComparison.Ordinal));
        Assert.True(Has(groceries.Assumptions, "Ask_Assume_SoFar"));

        var which = await _assistant.AskAsync("Hoeveel heb ik uitgegeven?");
        Assert.Equal(MissingValue.Category, which.Pending!.Missing);
        Assert.Contains("Abonnementen", which.Suggestions);
        var reply = await _assistant.AskAsync("Abonnementen", which.Pending);
        Assert.Equal((AssistantIntent.SpentOn, "Ask_Spent"), (reply.Intent, reply.Headline.Key));
    }

    // ===== What are my fixed costs? (D-039) =====

    [Fact]
    public async Task Fixed_costs_and_usual_spending_are_known_without_any_budget()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("Wat zijn mijn vaste kosten?");

        // Rent (planned) and the €15 subscription the app found itself; €350 usual day-to-day spending on top.
        Assert.Equal((AssistantIntent.FixedCosts, AnswerKind.Direct, "Ask_Fixed"), (answer.Intent, answer.Kind, answer.Headline.Key));
        Assert.Equal([Euro(915), Euro(350)], answer.Headline.Args);
        Assert.Equal(Euro(2_400), Figure(answer, "Ask_F_IncomeMonthly"));
        Assert.Equal(Euro(2_400 - 915 - 350), Figure(answer, "Ask_F_LeftMonthly"));
        Assert.Equal(Euro(670), Figure(answer, "Ask_F_UsualThisMonth"));
        Assert.Equal(("Ask_Fixed_Item", (object)"Huur"), (answer.Considered[0].Key, answer.Considered[0].Args[0]));
        Assert.Contains(answer.Considered, t => t.Args[0] is AnswerText { Key: "Ask_Name_Detected" } n && (string)n.Args[0] == "Abonnementen");
        Assert.True(Has(answer.Assumptions, "Ask_Assume_Learned"));
        Assert.Equal([AssistantAction.OpenForecasts], answer.Actions);
    }

    // ===== What changed versus last month? =====

    [Fact]
    public async Task Changed_versus_last_month_compares_the_last_two_complete_months()
    {
        await SetUpAsync();
        var answer = await _assistant.AskAsync("What changed versus last month?");

        Assert.Equal((AssistantIntent.ChangedVsLastMonth, "Ask_Changed_More"), (answer.Intent, answer.Headline.Key));
        Assert.Equal([new MonthRef(new(2026, 9, 1)), Euro(100), new MonthRef(new(2026, 8, 1))], answer.Headline.Args);
        var up = Assert.Single(answer.Considered);
        Assert.Equal(("Ask_Cat_Up", (object)"Boodschappen"), (up.Key, up.Args[0]));
    }

    // ===== Should I use VISA or wait? =====

    [Fact]
    public async Task Card_or_wait_recommends_the_card_only_when_its_settlement_is_covered()
    {
        await SetUpAsync();

        // From the current account the buffer breaks on the 15th; the statement on 5 November comes after the salary.
        var card = await _assistant.AskAsync("Moet ik de laptop met VISA betalen of wachten?");
        Assert.Equal((AssistantIntent.CardOrWait, "Ask_Card_UseCard"), (card.Intent, card.Headline.Key));
        Assert.Equal(["VISA", new DateOnly(2026, 11, 5), Euro(1_100)], card.Headline.Args.Skip(1));
        Assert.Equal("Ask_Assume_Credit", card.Assumptions[0].Key);

        var now = await _assistant.AskAsync("Should I use VISA or wait for €300?");
        Assert.Equal("Ask_Card_PayNow", now.Headline.Key);

        // Over the available credit (€1,100) the card is never recommended.
        var wait = await _assistant.AskAsync("Should I use VISA or wait for €1,200?");
        Assert.Equal("Ask_Card_Wait", wait.Headline.Key);
        Assert.Contains(wait.Figures, f => f.Value is AnswerText { Key: "Ask_V_Option" } v && ((AnswerText)v.Args[1]).Key == "Pay_CardOverLimit");
    }

    // ===== Help, not understood, no accounts =====

    [Fact]
    public async Task Help_and_unknown_questions_show_examples()
    {
        await SetUpAsync();

        var help = await _assistant.AskAsync("help");
        Assert.Equal((AnswerKind.Direct, "Ask_Help"), (help.Kind, help.Headline.Key));
        Assert.True(help.SuggestionsAreExamples);
        Assert.Equal(AssistantService.Examples, help.Suggestions);

        var unknown = await _assistant.AskAsync("Goedemorgen");
        Assert.Equal((AnswerKind.NotUnderstood, "Ask_NotUnderstood"), (unknown.Kind, unknown.Headline.Key));
    }

    [Fact]
    public async Task Without_accounts_every_forecast_question_says_what_is_missing()
    {
        foreach (var text in new[] { "Kan ik €100 uitgeven?", "Hoeveel kan ik veilig uitgeven?", "Welke betalingen komen eraan?", "Waarom is mijn saldo volgende maand lager?", "Wat als de huur €50 stijgt?", "Wat is er veranderd ten opzichte van vorige maand?" })
        {
            var answer = await _assistant.AskAsync(text);
            Assert.Equal(("Ask_NoAccount", AnswerKind.Qualified), (answer.Headline.Key, answer.Kind));
            Assert.Equal([AssistantAction.OpenAccounts], answer.Actions);
        }
    }

    // ===== Spec §15: never changes data; every text exists in both languages =====

    [Fact]
    public async Task Answers_never_change_data_and_every_text_has_a_translation()
    {
        await SetUpAsync();
        var balances = await _ledger.GetBalancesAsync();
        var rules = (await _recurring.ListRulesAsync()).Select(r => (r.Id, r.Amount)).ToList();
        var keys = StringKeys();

        var questions = new[]
        {
            "Can I afford this?", "Kan ik €500 uitgeven?", "Can I afford €700?", "Kan ik €2.500 betalen?",
            "Can I spend another €100 on groceries this month?", "Kan ik nog €200 uitgeven aan boodschappen?", "Kan ik nog €20 uitgeven aan abonnementen?",
            "How much can I safely spend before payday?", "What payments are coming up?", "Why will my balance be lower next month?",
            "What if renovation costs increase by €5,000?", "What if my salary drops by 200 euro per month?", "Wat als ik €100 per maand meer uitgeef?",
            "When can I afford the laptop?", "Wanneer kan ik €9.000 betalen?", "How much did I spend on subscriptions?",
            "Hoeveel heb ik dit jaar uitgegeven aan boodschappen?", "What changed versus last month?", "Should I use VISA or wait?",
            "Moet ik de laptop met VISA betalen of wachten?", "Should I use VISA or wait for €1,200?", "help", "Goedemorgen",
        };
        foreach (var text in questions)
        {
            var answer = await _assistant.AskAsync(text);
            foreach (var key in Keys(answer))
            {
                Assert.True(keys.Contains(key), $"'{text}' uses the missing text '{key}'.");
            }
        }

        Assert.Equal(balances, await _ledger.GetBalancesAsync());
        Assert.Equal(rules, (await _recurring.ListRulesAsync()).Select(r => (r.Id, r.Amount)));
        Assert.Empty(await _scenarios.ListAsync());
    }

    private static IEnumerable<string> Keys(AssistantAnswer answer)
    {
        var texts = new List<AnswerText> { answer.Headline };
        texts.AddRange(answer.Figures.Select(f => f.Label));
        texts.AddRange(answer.Figures.Select(f => f.Value).OfType<AnswerText>());
        texts.AddRange(answer.Considered);
        texts.AddRange(answer.Assumptions);
        if (answer.Explanation is { } e)
        {
            texts.Add(e);
        }

        foreach (var key in texts.SelectMany(Flatten))
        {
            yield return key;
        }

        foreach (var action in answer.Actions)
        {
            yield return "Ask_Action_" + action;
        }

        if (answer.SuggestionsAreExamples)
        {
            foreach (var s in answer.Suggestions)
            {
                yield return s;
            }
        }
    }

    private static IEnumerable<string> Flatten(AnswerText text) =>
        new[] { text.Key }.Concat(text.Args.OfType<AnswerText>().SelectMany(Flatten));

    private static HashSet<string> StringKeys()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "strings.tsv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadLines(Path.Combine(dir.FullName, "docs", "strings.tsv"))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 3 && p[1].Length > 0 && p[2].Length > 0)
            .Select(p => p[0])
            .ToHashSet();
    }
}
