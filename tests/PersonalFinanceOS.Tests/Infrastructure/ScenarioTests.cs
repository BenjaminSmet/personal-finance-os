using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Domain.Wishlist;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// Phase 7 against a real migrated database: what-if scenarios (spec §16). The fixture: €2,000 on the current account
/// today (10 October), €500 buffer, rent €900 on the 15th, salary €2,400 on the 25th, a €400 VISA purchase that is
/// debited on 5 November, €5,000 savings and a joint account that receives €600 on the 1st of every month.
/// </summary>
public sealed class ScenarioTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 9, 1);
    private static readonly DateOnly Today = new(2026, 10, 10);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly AppSettingsService _settings;
    private readonly WishlistService _wishlist;
    private readonly ScenarioService _scenarios;

    public ScenarioTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        var wishlistRepository = new WishlistRepository(db);
        _settings = new AppSettingsService(new SettingsStore(db, _clock));
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, _recurring, audit, _clock);
        var forecast = new ForecastService(accountRepository, _ledger, _recurring, cards, _settings, _clock);
        _wishlist = new WishlistService(wishlistRepository, transactions, _ledger, audit, _clock);
        _scenarios = new ScenarioService(new ScenarioRepository(db), accountRepository, wishlistRepository, _recurring, forecast, cards, audit, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static Money Euro(long euros) => Money.FromCents(euros * 100);

    private sealed record Fixture(Account Current, Account Visa, Account Savings, Account Joint, RecurringRule Rent, RecurringRule Salary, RecurringRule ToJoint);

    private async Task<Fixture> SetUpAsync()
    {
        var current = await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Euro(2_000), Share.Full));
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Euro(1_500), 28, 5, current.Id)));
        var savings = await _accounts.CreateAsync(new CreateAccountRequest("Spaarrekening", AccountType.Savings, Opening, Euro(5_000), Share.Full));
        var joint = await _accounts.CreateAsync(new CreateAccountRequest("Gezamenlijke rekening", AccountType.JointCurrent, Opening, Euro(300), new Share(5_000)));
        await _settings.SetMinimumBufferAsync(Euro(500));
        var rent = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, null, Euro(900), RecurrenceFrequency.Monthly, new(2026, 10, 15)));
        var salary = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, null, Euro(2_400), RecurrenceFrequency.Monthly, new(2026, 10, 25)));
        var toJoint = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Naar gezamenlijk", RecurringKind.Transfer, current.Id, joint.Id, null, Euro(600), RecurrenceFrequency.Monthly, new(2026, 11, 1)));
        await _ledger.RecordAsync(new TransactionRequest(visa.Id, new(2026, 10, 2), Euro(400), TransactionType.Expense, "Winkel"));
        return new Fixture(current, visa, savings, joint, rent, salary, toJoint);
    }

    private static ScenarioChangeRequest OneOff(Guid account, long euros, DateOnly date, string name = "Renovatie", bool income = false) =>
        new(ScenarioChangeKind.OneOff, Euro(euros), date, name, income, AccountId: account);

    private static Money Spendable(ForecastTimeline t, DateOnly date) => t.Spendable().BalanceOn(date);

    // ===== Baseline (spec §16: 7/30/90 days, month by month for 12 months) =====

    [Fact]
    public async Task Without_changes_the_scenario_equals_the_baseline_for_a_year()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync((Guid?)null);

        Assert.True(result.CanCalculate);
        Assert.False(result.HasChanges);
        Assert.Equal(new DateOnly(2027, 9, 30), result.LastDay);
        Assert.Equal([7, 30, 90], result.Horizons.Select(h => h.Days));
        Assert.All(result.Horizons, h => Assert.True(h.Difference.IsZero));
        Assert.Equal(new DateOnly(2026, 10, 17), result.Horizons[0].Date);

        var months = result.Months();
        Assert.Equal(12, months.Count);
        Assert.Equal(new DateOnly(2026, 10, 1), months[0].Month);
        Assert.Equal(new DateOnly(2027, 9, 1), months[^1].Month);
        Assert.All(months, m => Assert.True(m.Difference.IsZero));

        // October: 2,000 − 900 rent + 2,400 salary; the lowest point is after the rent.
        Assert.Equal(Euro(3_500), months[0].Baseline);
        Assert.Equal(Euro(1_100), months[0].BaselineLowest);

        // November: − 600 to the joint account, − 400 VISA statement, − 900 rent, + 2,400 salary.
        Assert.Equal(Euro(4_000), months[1].Baseline);

        // Per account: the joint account receives the transfer, savings stays as it is.
        var joint = result.Months(f.Joint.Id);
        Assert.Equal((Euro(300), Euro(900)), (joint[0].Baseline, joint[1].Baseline));
        Assert.All(result.Months(f.Savings.Id), m => Assert.Equal(Euro(5_000), m.Baseline));

        Assert.Empty(result.BaselineShortfalls);
        Assert.Empty(result.ScenarioShortfalls);
    }

    // ===== Renovation costs, purchase timing (spec §16) =====

    [Fact]
    public async Task A_one_off_cost_shows_its_difference_and_new_shortfalls_per_account()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([OneOff(f.Current.Id, 5_000, new(2026, 11, 10))]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ScenarioNote.None, change.Note);
        Assert.Equal(Euro(-5_000), change.SpendableEffect);
        Assert.Equal(Money.Zero, change.OtherEffect);

        // 7 and 30 days ahead nothing changes yet; 90 days ahead it does.
        Assert.Equal([Money.Zero, Money.Zero, Euro(-5_000)], result.Horizons.Select(h => h.Difference));

        Assert.Equal(Euro(-5_000), result.Months()[1].Difference);
        Assert.Equal(Euro(-3_400), result.Months()[1].ScenarioLowest);

        // 3,500 − 600 − 400 = 2,500 on 5 November, − 5,000 on the 10th.
        Assert.Contains(result.ScenarioShortfalls, s => s is { Account: null, Kind: ShortfallKind.BelowBuffer } && s.Date == new DateOnly(2026, 11, 10) && s.Balance == Euro(-2_500));
        Assert.Contains(result.ScenarioShortfalls, s => s is { Account: null, Kind: ShortfallKind.BelowZero } && s.Date == new DateOnly(2026, 11, 10));
        Assert.Contains(result.ScenarioShortfalls, s => s.Account?.Id == f.Current.Id && s.Kind == ShortfallKind.BelowZero);
        Assert.DoesNotContain(result.ScenarioShortfalls, s => s.Account?.Id == f.Joint.Id);
        Assert.Empty(result.BaselineShortfalls);

        // Moving the purchase to April, after a few more salaries, keeps everything above the buffer.
        var later = await _scenarios.EvaluateAsync([OneOff(f.Current.Id, 5_000, new(2027, 4, 10))]);
        Assert.Empty(later.ScenarioShortfalls);
    }

    [Fact]
    public async Task A_date_in_the_past_counts_from_today_and_says_so()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([OneOff(f.Current.Id, 100, new(2026, 10, 1))]);

        Assert.Equal(ScenarioNote.DatePassed, result.Changes[0].Note);
        Assert.Equal(Euro(1_900), Spendable(result.Projected, Today));
    }

    [Fact]
    public async Task Savings_and_joint_changes_are_not_spendable_money()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([OneOff(f.Savings.Id, 1_000, new(2026, 10, 20), "Gift", income: true)]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(Money.Zero, change.SpendableEffect);
        Assert.Equal(Euro(1_000), change.OtherEffect);
        Assert.All(result.Months(), m => Assert.True(m.Difference.IsZero));
        Assert.Equal(Euro(1_000), result.Months(f.Savings.Id)[0].Difference);
    }

    // ===== Salary, recurring costs, joint transfers (spec §16) =====

    [Fact]
    public async Task A_lower_salary_from_next_month_changes_every_later_payment()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(2_000), new(2026, 11, 1), RuleId: f.Salary.Id)]);

        var change = Assert.Single(result.Changes);
        Assert.Equal("Loon", change.Name);

        // 25 November 2026 to 25 September 2027: eleven salaries, each €400 lower.
        Assert.Equal(Euro(-4_400), change.SpendableEffect);
        Assert.True(result.Months()[0].Difference.IsZero);
        Assert.Equal(Euro(-400), result.Months()[1].Difference);
        Assert.Equal(Euro(-4_400), result.Months()[^1].Difference);
    }

    [Fact]
    public async Task A_new_monthly_cost_and_an_ended_one()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync(
        [
            new ScenarioChangeRequest(ScenarioChangeKind.Monthly, Euro(100), new(2026, 11, 1), "Fitness", AccountId: f.Current.Id),
            new ScenarioChangeRequest(ScenarioChangeKind.Monthly, Euro(50), new(2026, 11, 1), "Opvang", EndDate: new(2027, 1, 31), AccountId: f.Current.Id),
        ]);

        Assert.Equal(Euro(-1_100), result.Changes[0].SpendableEffect);
        Assert.Equal(Euro(-150), result.Changes[1].SpendableEffect);
        Assert.Equal(Euro(-1_250), result.Months()[^1].Difference);

        // Stopping the rent from December: zero removes it.
        var noRent = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Money.Zero, new(2026, 12, 1), RuleId: f.Rent.Id)]);
        Assert.Equal(Euro(900 * 10), noRent.Changes[0].SpendableEffect);
    }

    [Fact]
    public async Task A_higher_joint_transfer_moves_money_between_accounts()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(800), Today, RuleId: f.ToJoint.Id)]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(Euro(-2_200), change.SpendableEffect);
        Assert.Equal(Euro(2_200), change.OtherEffect);
        Assert.Equal(Euro(-200), result.Months()[1].Difference);
        Assert.Equal(Euro(200), result.Months(f.Joint.Id)[1].Difference);
    }

    // ===== Payment method (spec §16) and the card's settlement =====

    [Fact]
    public async Task A_card_purchase_leaves_the_current_account_on_the_statement_debit_day()
    {
        var f = await SetUpAsync();
        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(700), Today, "Zetel", CardId: f.Visa.Id)]);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ScenarioNote.None, change.Note);
        Assert.Equal(new DateOnly(2026, 11, 5), change.PaidOn);
        Assert.Equal(Euro(-700), change.SpendableEffect);
        Assert.Equal(Spendable(result.Baseline, new(2026, 11, 4)), Spendable(result.Projected, new(2026, 11, 4)));
        Assert.Equal(Euro(-700), Spendable(result.Projected, new(2026, 11, 5)) - Spendable(result.Baseline, new(2026, 11, 5)));

        // Bought after the statement closes on the 28th, it is paid a month later.
        var afterClose = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(700), new(2026, 10, 30), "Zetel", CardId: f.Visa.Id)]);
        Assert.Equal(new DateOnly(2026, 12, 5), afterClose.Changes[0].PaidOn);

        // Paid from the current account it leaves on the day itself.
        var cash = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(700), Today, "Zetel", AccountId: f.Current.Id)]);
        Assert.Equal(Today, cash.Changes[0].PaidOn);
    }

    [Fact]
    public async Task The_card_limit_is_not_money_a_purchase_over_it_is_flagged()
    {
        var f = await SetUpAsync();

        // €1,500 limit, €400 used: €1,100 available.
        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(1_200), Today, "Tv", CardId: f.Visa.Id)]);
        Assert.Equal(ScenarioNote.OverLimit, result.Changes[0].Note);
    }

    [Fact]
    public async Task A_higher_card_subscription_raises_each_statement_and_a_payment_to_the_card_changes_nothing()
    {
        var f = await SetUpAsync();
        var streaming = await _recurring.CreateRuleAsync(new RecurringRuleRequest("Streaming", RecurringKind.Expense, f.Visa.Id, null, null, Euro(15), RecurrenceFrequency.Monthly, new(2026, 10, 20)));
        var toCard = await _recurring.CreateRuleAsync(new RecurringRuleRequest("VISA aflossen", RecurringKind.Transfer, f.Current.Id, f.Visa.Id, null, Euro(400), RecurrenceFrequency.Monthly, new(2026, 11, 5)));

        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(25), Today, RuleId: streaming.Id)]);
        var change = Assert.Single(result.Changes);
        Assert.Equal(ScenarioNote.None, change.Note);

        // Each statement is €10 higher, paid on the 5th: 5 November 2026 up to 5 September 2027.
        Assert.Equal(Euro(-110), change.SpendableEffect);
        Assert.Equal(Euro(-10), Spendable(result.Projected, new(2026, 11, 5)) - Spendable(result.Baseline, new(2026, 11, 5)));
        Assert.True((Spendable(result.Projected, new(2026, 11, 4)) - Spendable(result.Baseline, new(2026, 11, 4))).IsZero);

        var payment = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(1_000), Today, RuleId: toCard.Id)]);
        Assert.Equal(ScenarioNote.PaymentToCard, payment.Changes[0].Note);
        Assert.True(payment.Changes[0].SpendableEffect.IsZero);
    }

    // ===== Wishlist purchases (spec §16) =====

    [Fact]
    public async Task Buying_a_wishlist_item_shows_what_it_does_to_the_other_items()
    {
        var f = await SetUpAsync();
        var sofa = await _wishlist.CreateAsync(new WishlistRequest("Zetel", Euro(1_500)));
        var bike = await _wishlist.CreateAsync(new WishlistRequest("Fiets", Euro(500)));

        var result = await _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(1_500), Today, AccountId: f.Current.Id, WishlistItemId: sofa.Id)]);

        Assert.Equal("Zetel", result.Changes[0].Name);
        var effect = Assert.Single(result.Wishlist);
        Assert.Equal(bike.Id, effect.Item.Id);
        Assert.Equal(Today, effect.Before);
        Assert.True(effect.IsDelayed);
        Assert.True(effect.After > Today);

        // Only planned: the item stays on the wishlist.
        Assert.Equal(WishlistStatus.Desired, (await _wishlist.ListAsync()).Single(e => e.Item.Id == sofa.Id).Item.Status);
    }

    // ===== Saved scenarios never change the ledger (spec §16) =====

    [Fact]
    public async Task Saved_scenarios_are_stored_with_their_changes_and_never_touch_the_ledger()
    {
        var f = await SetUpAsync();
        var balances = await _ledger.GetBalancesAsync();
        var rules = await _recurring.ListRulesAsync();
        var changed = 0;
        _scenarios.Changed += (_, _) => changed++;

        var scenario = await _scenarios.CreateAsync("Verbouwing duurder", "Offerte aannemer", [OneOff(f.Current.Id, 5_000, new(2026, 11, 10))]);
        await _scenarios.AddChangeAsync(scenario.Id, new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(2_000), new(2026, 11, 1), RuleId: f.Salary.Id));
        Assert.Equal(2, (await _scenarios.ListChangesAsync(scenario.Id)).Count);

        var saved = await _scenarios.EvaluateAsync(scenario.Id);
        var unsaved = await _scenarios.EvaluateAsync(
        [
            OneOff(f.Current.Id, 5_000, new(2026, 11, 10)),
            new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(2_000), new(2026, 11, 1), RuleId: f.Salary.Id),
        ]);
        Assert.Equal(unsaved.Months().Select(m => m.Scenario), saved.Months().Select(m => m.Scenario));

        await _scenarios.RenameAsync(scenario.Id, "Verbouwing + minder loon", null);
        var listed = Assert.Single(await _scenarios.ListAsync());
        Assert.Equal(("Verbouwing + minder loon", (string?)null), (listed.Name, listed.Note));

        var first = (await _scenarios.ListChangesAsync(scenario.Id))[0];
        await _scenarios.RemoveChangeAsync(scenario.Id, first.Id);
        Assert.Single(await _scenarios.ListChangesAsync(scenario.Id));

        await _scenarios.RemoveAsync(scenario.Id);
        Assert.Empty(await _scenarios.ListAsync());
        Assert.Empty(await _scenarios.ListChangesAsync(scenario.Id));
        Assert.Equal(5, changed);

        // Nothing in the ledger or the planning changed.
        Assert.Equal(balances, await _ledger.GetBalancesAsync());
        Assert.Equal(rules.Select(r => (r.Id, r.Amount, r.EndDate)), (await _recurring.ListRulesAsync()).Select(r => (r.Id, r.Amount, r.EndDate)));
    }

    [Fact]
    public async Task Invalid_changes_are_refused_with_a_reason()
    {
        var f = await SetUpAsync();

        await AssertCode("scenario.name.required", () => _scenarios.CreateAsync("  "));
        await AssertCode("scenario.account.required", () => _scenarios.EvaluateAsync([OneOff(f.Visa.Id, 100, Today)]));
        await AssertCode("scenario.account.required", () => _scenarios.EvaluateAsync([OneOff(Guid.NewGuid(), 100, Today)]));
        await AssertCode("scenario.payment.required", () => _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(100), Today, "Tv")]));
        await AssertCode("scenario.payment.required", () => _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.Purchase, Euro(100), Today, "Tv", CardId: f.Current.Id)]));
        await AssertCode("scenario.planned.required", () => _scenarios.EvaluateAsync([new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, Euro(100), Today, RuleId: Guid.NewGuid())]));
        await AssertCode("scenario.change.name.required", () => _scenarios.EvaluateAsync([OneOff(f.Current.Id, 100, Today, name: "")]));
        await AssertCode("transaction.amount.zero", () => _scenarios.EvaluateAsync([OneOff(f.Current.Id, 0, Today)]));
        await AssertCode("recurring.endBeforeStart", () => _scenarios.EvaluateAsync(
            [new ScenarioChangeRequest(ScenarioChangeKind.Monthly, Euro(10), new(2026, 12, 1), "X", EndDate: new(2026, 11, 1), AccountId: f.Current.Id)]));
        await AssertCode("scenario.notFound", () => _scenarios.RenameAsync(Guid.NewGuid(), "X", null));

        // A scenario with a refused change is not saved at all.
        await AssertCode("scenario.account.required", () => _scenarios.CreateAsync("Kapot", null, [OneOff(f.Visa.Id, 100, Today)]));
        Assert.Empty(await _scenarios.ListAsync());
    }

    [Fact]
    public async Task Without_a_current_account_there_is_nothing_to_compare()
    {
        var result = await _scenarios.EvaluateAsync((Guid?)null);
        Assert.False(result.CanCalculate);
        Assert.Empty(result.Wishlist);
    }

    private static async Task AssertCode(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainValidationException>(action)).Code);
}
