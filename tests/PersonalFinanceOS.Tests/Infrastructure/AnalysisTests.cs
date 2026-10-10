using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Phase 5 against a real migrated database: categories, budgets and the monthly analysis (spec §13, §23 reports).</summary>
public sealed class AnalysisTests : IDisposable
{
    private static readonly DateOnly October = new(2026, 10, 1);
    private static readonly DateOnly September = new(2026, 9, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly Func<FinanceDbContext> _db;
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly CategoryService _categories;
    private readonly BudgetService _budgets;
    private readonly RecurringService _recurring;
    private readonly AnalysisService _analysis;

    public AnalysisTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        _db = () => new FinanceDbContext(options);
        var audit = new AuditLog(_db, _clock);
        var accountRepository = new AccountRepository(_db);
        var categoryRepository = new CategoryRepository(_db);
        var budgetRepository = new BudgetRepository(_db);
        var transactions = new TransactionRepository(_db);
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _categories = new CategoryService(categoryRepository, budgetRepository, new SettingsStore(_db, _clock), audit, _clock);
        _budgets = new BudgetService(budgetRepository, categoryRepository, audit, _clock);
        _recurring = new RecurringService(new RecurringRepository(_db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        _analysis = new AnalysisService(accountRepository, categoryRepository, transactions, budgetRepository, _recurring, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private Task<Account> AccountAsync(string name, AccountType type, DateOnly opening, Share? share = null, CardSettings? card = null) =>
        _accounts.CreateAsync(new CreateAccountRequest(name, type, opening, Money.Zero, share ?? Share.Full, Card: card));

    private Task<Transaction> SpendAsync(Account account, DateOnly date, long cents, Category? category = null, string text = "Winkel") =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, date, Money.FromCents(cents), TransactionType.Expense, text, category?.Id));

    private Task<Transaction> RecordAsync(Account account, DateOnly date, long cents, TransactionType type, Category? category = null) =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, date, Money.FromCents(cents), type, type.ToString(), category?.Id));

    private static CategoryAmount For(MonthAnalysis analysis, Category? category) =>
        analysis.Expenses.Single(e => e.Category?.Id == category?.Id);

    // ===== Totals reconcile with the ledger (spec §23 reports) =====

    [Fact]
    public async Task Month_totals_and_categories_reconcile_with_the_ledger_and_never_count_transfers()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var savings = await AccountAsync("Spaarrekening", AccountType.Savings, new(2026, 6, 1));
        var visa = await AccountAsync("VISA", AccountType.CreditCard, new(2026, 6, 1), card: new CardSettings(Money.FromCents(150_000), 28, 5, current.Id));
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);

        await RecordAsync(current, new(2026, 10, 1), 240_000, TransactionType.Income, salary);
        await SpendAsync(current, new(2026, 10, 2), 8_000, groceries);
        await RecordAsync(current, new(2026, 10, 3), 1_500, TransactionType.Refund, groceries);
        await SpendAsync(visa, new(2026, 10, 4), 4_000);
        await SpendAsync(visa, new(2026, 9, 30), 12_000, groceries); // purchase month counts, not the settlement month
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, savings.Id, new(2026, 10, 5), Money.FromCents(50_000), "Sparen"));
        await _ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, new(2026, 10, 5), Money.FromCents(12_000), "VISA"));

        var october = await _analysis.AnalyzeMonthAsync(new(2026, 10, 20));
        var ledger = await _ledger.GetTotalsAsync(October, new(2026, 10, 31));

        Assert.Equal(October, october.Month);
        Assert.True(october.IsCurrentMonth);
        Assert.Equal(ledger, october.Totals);
        Assert.Equal(Money.FromCents(10_500), october.Totals.Expenses); // 80 − 15 refund + 40 card; transfers never
        Assert.Equal(october.Totals.Expenses, Money.Sum(october.Expenses.Select(e => e.Actual)));
        Assert.Equal(october.Totals.Income, Money.Sum(october.Income.Select(e => e.Actual)));
        Assert.Equal(Money.FromCents(6_500), For(october, groceries).Actual);
        Assert.Equal(Money.FromCents(4_000), For(october, null).Actual); // without category, still counted

        var september = await _analysis.AnalyzeMonthAsync(September);
        Assert.Equal(Money.FromCents(12_000), For(september, groceries).Actual);
        Assert.False(september.IsCurrentMonth);
    }

    // ===== Budgets =====

    [Fact]
    public async Task A_budget_holds_from_its_month_and_changing_it_never_rewrites_earlier_months()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);

        await _budgets.SetAsync(groceries.Id, new(2026, 8, 15), Money.FromCents(30_000));
        Assert.Null(For(await _analysis.AnalyzeMonthAsync(new(2026, 7, 1)), groceries).Budget);
        Assert.Equal(Money.FromCents(30_000), For(await _analysis.AnalyzeMonthAsync(September), groceries).Budget);

        await _budgets.SetAsync(groceries.Id, October, Money.FromCents(25_000));
        Assert.Equal(Money.FromCents(30_000), For(await _analysis.AnalyzeMonthAsync(September), groceries).Budget);
        Assert.Equal(Money.FromCents(25_000), For(await _analysis.AnalyzeMonthAsync(October), groceries).Budget);

        // A budget set for a later month is replaced when an earlier month is changed: it holds until changed again.
        await _budgets.SetAsync(groceries.Id, new(2026, 12, 1), Money.FromCents(40_000));
        await _budgets.SetAsync(groceries.Id, October, Money.FromCents(26_000));
        Assert.Equal(Money.FromCents(26_000), For(await _analysis.AnalyzeMonthAsync(new(2027, 1, 1)), groceries).Budget);

        // Zero: no budget from that month on; earlier months keep theirs.
        await _budgets.SetAsync(groceries.Id, new(2026, 11, 1), Money.Zero);
        Assert.Null(For(await _analysis.AnalyzeMonthAsync(new(2026, 11, 1)), groceries).Budget);
        Assert.Equal(Money.FromCents(26_000), For(await _analysis.AnalyzeMonthAsync(October), groceries).Budget);
        Assert.Equal(3, _temp.Scalar("SELECT COUNT(*) FROM budgets;"));
    }

    [Fact]
    public async Task Budgets_are_only_for_active_expense_categories_and_never_negative()
    {
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);
        var leisure = await _categories.CreateAsync("Vrije tijd", CategoryKind.Expense);
        Assert.Equal("budget.category.invalid", (await Assert.ThrowsAsync<DomainValidationException>(() => _budgets.SetAsync(salary.Id, October, Money.FromCents(100)))).Code);
        Assert.Equal("budget.amount.negative", (await Assert.ThrowsAsync<DomainValidationException>(() => _budgets.SetAsync(leisure.Id, October, Money.FromCents(-1)))).Code);
        await _categories.ArchiveAsync(leisure.Id);
        Assert.Equal("budget.category.invalid", (await Assert.ThrowsAsync<DomainValidationException>(() => _budgets.SetAsync(leisure.Id, October, Money.FromCents(100)))).Code);
    }

    [Fact]
    public async Task Actual_versus_budget_shows_what_is_left_and_what_is_over()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var leisure = await _categories.CreateAsync("Vrije tijd", CategoryKind.Expense);
        await _categories.CreateAsync("Verzekeringen", CategoryKind.Expense);
        await _budgets.SetAsync(groceries.Id, October, Money.FromCents(30_000));
        await _budgets.SetAsync(leisure.Id, October, Money.FromCents(5_000));
        await SpendAsync(current, new(2026, 10, 2), 12_000, groceries);
        await SpendAsync(current, new(2026, 10, 3), 7_500, leisure);
        await SpendAsync(current, new(2026, 10, 4), 2_000); // no category: no budget

        var analysis = await _analysis.AnalyzeMonthAsync(October);
        var g = For(analysis, groceries);
        Assert.Equal(Money.FromCents(18_000), g.Remaining);
        Assert.Equal(40, g.UsedPercent);
        Assert.False(g.IsOverBudget);
        var l = For(analysis, leisure);
        Assert.True(l.IsOverBudget);
        Assert.Equal(Money.FromCents(-2_500), l.Remaining);
        Assert.Equal(150, l.UsedPercent);
        Assert.Equal(Money.FromCents(35_000), analysis.BudgetTotal);
        Assert.Equal(Money.FromCents(19_500), analysis.BudgetedSpending);
        Assert.Equal([leisure.Id], analysis.OverBudget.Select(e => e.Category!.Id));

        // Every active expense category is listed, so a budget can be set before anything is spent.
        Assert.Equal(4, analysis.Expenses.Count);
    }

    // ===== History: comparisons only where data exists (spec §13, §18) =====

    [Fact]
    public async Task Without_enough_history_there_is_no_comparison_and_nothing_is_called_unusual()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 9, 15));
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        await SpendAsync(current, new(2026, 9, 20), 10_000, groceries);
        await SpendAsync(current, new(2026, 10, 2), 90_000, groceries);

        var october = await _analysis.AnalyzeMonthAsync(October);
        Assert.Equal(0, october.ComparedMonths); // September started on the 15th: incomplete, never compared
        Assert.Null(october.PreviousAverage);
        Assert.False(october.CanShowUnusual);
        Assert.Null(For(october, groceries).PreviousAverage);
        Assert.False(For(october, groceries).IsUnusual);
        Assert.False(october.IsIncomplete);

        var september = await _analysis.AnalyzeMonthAsync(September);
        Assert.True(september.IsIncomplete);
        Assert.Equal(new DateOnly(2026, 9, 15), september.HistoryStart);

        // Trend: months before the first balance date have no data.
        Assert.Equal(6, october.Trend.Count);
        Assert.Equal([false, false, false, false, true, true], october.Trend.Select(p => p.HasData));
        Assert.True(october.Trend[4].IsPartial);
        Assert.True(october.Trend[5].IsPartial); // the month in progress
    }

    [Fact]
    public async Task With_three_complete_months_a_clear_change_is_flagged_and_a_month_in_progress_is_never_called_low()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var groceries = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var leisure = await _categories.CreateAsync("Vrije tijd", CategoryKind.Expense);
        var transport = await _categories.CreateAsync("Vervoer", CategoryKind.Expense);
        foreach (var month in new[] { 7, 8, 9 })
        {
            await SpendAsync(current, new(2026, month, 5), 20_000, groceries);
            await SpendAsync(current, new(2026, month, 6), 8_000, leisure);
            await SpendAsync(current, new(2026, month, 7), 6_000, transport);
        }

        await SpendAsync(current, new(2026, 10, 5), 40_000, groceries); // double
        await SpendAsync(current, new(2026, 10, 6), 1_000, leisure);    // much lower, but October is not over
        await SpendAsync(current, new(2026, 10, 7), 7_000, transport);  // +17 %: normal

        var october = await _analysis.AnalyzeMonthAsync(October);
        Assert.Equal(3, october.ComparedMonths);
        Assert.Equal(Money.FromCents(34_000), october.PreviousAverage!.Value.Expenses);
        var g = For(october, groceries);
        Assert.Equal(Money.FromCents(20_000), g.PreviousAverage);
        Assert.Equal(100, g.ChangePercent);
        Assert.True(g.IsUnusual);
        Assert.False(For(october, leisure).IsUnusual);
        Assert.False(For(october, transport).IsUnusual);

        // A finished month can be unusually low. September against June (no data yet), July and August: only 2 months.
        await SpendAsync(current, new(2026, 6, 5), 20_000, groceries);
        await SpendAsync(current, new(2026, 6, 6), 8_000, leisure);
        await SpendAsync(current, new(2026, 9, 8), 0_100, transport);
        var september = await _analysis.AnalyzeMonthAsync(September);
        Assert.Equal(3, september.ComparedMonths);
        Assert.False(For(september, leisure).IsUnusual);
    }

    // ===== Recurring vs one-off, planned vs actual, shared accounts =====

    [Fact]
    public async Task Recurring_spending_is_what_settled_a_planned_item_and_planned_items_show_what_happened()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var rent = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, null, Money.FromCents(85_000), RecurrenceFrequency.Monthly, October));
        var gym = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Fitness", RecurringKind.Expense, current.Id, null, null, Money.FromCents(3_000), RecurrenceFrequency.Monthly, new(2026, 10, 3)));
        var phone = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Telefoon", RecurringKind.Expense, current.Id, null, null, Money.FromCents(2_000), RecurrenceFrequency.Monthly, new(2026, 10, 8)));
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Verzekering", RecurringKind.Expense, current.Id, null, null, Money.FromCents(4_000), RecurrenceFrequency.Monthly, new(2026, 10, 25)));

        await _recurring.ConfirmAsync(rent.Id, October, October, Money.FromCents(86_500));
        var phoneBill = await SpendAsync(current, new(2026, 10, 9), 2_100, text: "Telefoon");
        await _recurring.LinkAsync(phone.Id, new(2026, 10, 8), phoneBill.Id);
        await _recurring.SkipAsync(gym.Id, new(2026, 10, 3));
        await SpendAsync(current, new(2026, 10, 4), 4_200, text: "Bakker");

        var october = await _analysis.AnalyzeMonthAsync(October);
        Assert.Equal(Money.FromCents(88_600), october.RecurringSpending);
        Assert.Equal(Money.FromCents(4_200), october.OneOffSpending);
        Assert.Equal(october.Totals.Expenses, october.RecurringSpending + october.OneOffSpending);

        Assert.Equal(["Huur", "Fitness", "Telefoon", "Verzekering"], october.Planned.Select(p => p.Rule.Name));
        var r = october.Planned[0];
        Assert.Equal(OutcomeState.Confirmed, r.State);
        Assert.Equal(Money.FromCents(1_500), r.Difference);
        Assert.Equal(OutcomeState.Skipped, october.Planned[1].State);
        Assert.Equal(OutcomeState.Confirmed, october.Planned[2].State);
        Assert.Equal(phoneBill.Id, october.Planned[2].Actual!.Id);
        Assert.Equal(OutcomeState.Open, october.Planned[3].State);
        Assert.Null(october.Planned[3].Difference);

        // Removing the confirmed transaction makes the item open again and the spending one-off no more.
        await _ledger.VoidAsync(r.Actual!.Id);
        october = await _analysis.AnalyzeMonthAsync(October);
        Assert.Equal(OutcomeState.Open, october.Planned[0].State);
        Assert.Equal(Money.FromCents(2_100), october.RecurringSpending);
    }

    [Fact]
    public async Task Shared_accounts_show_full_spending_and_the_personal_part()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var joint = await AccountAsync("Gezamenlijk", AccountType.JointCurrent, new(2026, 6, 1), Share.FromPercent(50));
        await SpendAsync(joint, new(2026, 10, 2), 12_345);

        var shared = Assert.Single((await _analysis.AnalyzeMonthAsync(October)).Shared);
        Assert.Equal(joint.Id, shared.Account.Id);
        Assert.Equal(Money.FromCents(12_345), shared.Spending);
        Assert.Equal(joint.PersonalPart(Money.FromCents(12_345)), shared.PersonalPart);
    }

    // ===== Categories: merge, reorder, bring back (spec §13) =====

    [Fact]
    public async Task Merging_moves_everything_to_the_target_in_one_go_and_archives_the_source()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        var restaurants = await _categories.CreateAsync("Restaurants", CategoryKind.Expense);
        var leisure = await _categories.CreateAsync("Vrije tijd", CategoryKind.Expense);
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);
        await SpendAsync(current, new(2026, 10, 2), 4_500, restaurants);
        await SpendAsync(current, new(2026, 10, 3), 2_000, leisure);
        await new PayeeRuleRepository(_db).SaveAsync(PayeeRule.Learn("Pizzeria", TransactionType.Expense, restaurants.Id, current.Id, _clock.Now));
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Etentje", RecurringKind.Expense, current.Id, null, restaurants.Id, Money.FromCents(5_000), RecurrenceFrequency.Monthly, October));
        await _budgets.SetAsync(restaurants.Id, October, Money.FromCents(10_000));
        var updatedBefore = _temp.Scalar("SELECT COUNT(*) FROM transactions WHERE updated_at = created_at;");

        Assert.Equal("category.merge.invalid", (await Assert.ThrowsAsync<DomainValidationException>(() => _categories.MergeAsync(restaurants.Id, salary.Id))).Code);
        Assert.Equal("category.merge.invalid", (await Assert.ThrowsAsync<DomainValidationException>(() => _categories.MergeAsync(restaurants.Id, restaurants.Id))).Code);

        await _categories.MergeAsync(restaurants.Id, leisure.Id);

        Assert.DoesNotContain(await _categories.ListAsync(), c => c.Id == restaurants.Id);
        Assert.Equal(0, _temp.Scalar($"SELECT COUNT(*) FROM transactions WHERE category_id = '{restaurants.Id:D}';"));
        Assert.Equal(0, _temp.Scalar($"SELECT COUNT(*) FROM payee_rules WHERE category_id = '{restaurants.Id:D}';"));
        Assert.Equal(0, _temp.Scalar($"SELECT COUNT(*) FROM recurring_rules WHERE category_id = '{restaurants.Id:D}';"));
        Assert.Equal(updatedBefore, _temp.Scalar("SELECT COUNT(*) FROM transactions WHERE updated_at = created_at;"));

        var october = await _analysis.AnalyzeMonthAsync(October);
        Assert.Equal(Money.FromCents(6_500), For(october, leisure).Actual);
        Assert.Equal(Money.FromCents(10_000), For(october, leisure).Budget); // the target had none, so it took the source's
        Assert.DoesNotContain(october.Expenses, e => e.Category?.Id == restaurants.Id);
    }

    [Fact]
    public async Task When_both_have_a_budget_the_target_keeps_its_own()
    {
        var a = await _categories.CreateAsync("Restaurants", CategoryKind.Expense);
        var b = await _categories.CreateAsync("Vrije tijd", CategoryKind.Expense);
        await _budgets.SetAsync(a.Id, October, Money.FromCents(10_000));
        await _budgets.SetAsync(b.Id, October, Money.FromCents(7_000));

        await _categories.MergeAsync(a.Id, b.Id);

        Assert.Equal(1, _temp.Scalar("SELECT COUNT(*) FROM budgets;"));
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, new(2026, 6, 1));
        Assert.Equal(Money.FromCents(7_000), For(await _analysis.AnalyzeMonthAsync(October), b).Budget);
    }

    [Fact]
    public async Task Categories_move_up_and_down_within_their_kind_and_can_be_brought_back()
    {
        var a = await _categories.CreateAsync("Wonen", CategoryKind.Expense);
        var salary = await _categories.CreateAsync("Loon", CategoryKind.Income);
        var b = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense);
        var c = await _categories.CreateAsync("Vervoer", CategoryKind.Expense);

        await _categories.MoveAsync(c.Id, -1);
        Assert.Equal(["Wonen", "Vervoer", "Boodschappen"], (await _categories.ListAsync()).Where(x => x.Kind == CategoryKind.Expense).Select(x => x.Name));
        await _categories.MoveAsync(a.Id, -1); // already first: nothing happens
        await _categories.MoveAsync(a.Id, +1);
        Assert.Equal(["Vervoer", "Wonen", "Boodschappen"], (await _categories.ListAsync()).Where(x => x.Kind == CategoryKind.Expense).Select(x => x.Name));
        Assert.Equal([salary.Id], (await _categories.ListAsync()).Where(x => x.Kind == CategoryKind.Income).Select(x => x.Id));

        await _categories.ArchiveAsync(b.Id);
        var again = await _categories.CreateAsync("Boodschappen", CategoryKind.Expense); // the name is free again
        Assert.Equal("category.name.duplicate", (await Assert.ThrowsAsync<DomainValidationException>(() => _categories.ReactivateAsync(b.Id))).Code);

        await _categories.ArchiveAsync(again.Id);
        await _categories.ReactivateAsync(b.Id);
        Assert.Equal(["Vervoer", "Wonen", "Boodschappen"], (await _categories.ListAsync()).Where(x => x.Kind == CategoryKind.Expense).Select(x => x.Name));
        Assert.Equal(b.Id, (await _categories.ListAsync()).Last(x => x.Kind == CategoryKind.Expense).Id);
    }
}
