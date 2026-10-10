using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Domain.Wishlist;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// Phase 6 against a real migrated database: the wishlist and its affordability (spec §14, §23 wishlist). The fixture:
/// €2,000 on the current account today (10 October), €500 buffer, rent €900 on the 15th, salary €2,400 on the 25th and
/// a €400 VISA purchase that is debited on 5 November.
/// </summary>
public sealed class WishlistTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 9, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly AppSettingsService _settings;
    private readonly AffordabilityService _affordability;
    private readonly WishlistService _wishlist;

    public WishlistTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        _settings = new AppSettingsService(new SettingsStore(db, _clock));
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        var cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, _recurring, audit, _clock);
        var forecast = new ForecastService(accountRepository, _ledger, _recurring, cards, _settings, _clock);
        _affordability = new AffordabilityService(forecast, cards);
        _wishlist = new WishlistService(new WishlistRepository(db), transactions, _ledger, audit, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static Money Euro(long euros) => Money.FromCents(euros * 100);

    private async Task<(Account Current, Account Visa)> SetUpAsync(bool salary = true)
    {
        var current = await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Euro(2_000), Share.Full));
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Euro(1_500), 28, 5, current.Id)));
        await _settings.SetMinimumBufferAsync(Euro(500));
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Huur", RecurringKind.Expense, current.Id, null, null, Euro(900), RecurrenceFrequency.Monthly, new(2026, 10, 15)));
        if (salary)
        {
            await _recurring.CreateRuleAsync(new RecurringRuleRequest("Loon", RecurringKind.Income, current.Id, null, null, Euro(2_400), RecurrenceFrequency.Monthly, new(2026, 10, 25)));
        }

        await _ledger.RecordAsync(new TransactionRequest(visa.Id, new(2026, 10, 2), Euro(400), TransactionType.Expense, "Winkel"));
        return (current, visa);
    }

    private Task<WishlistItem> ItemAsync(string name, long euros, DateOnly? target = null) =>
        _wishlist.CreateAsync(new WishlistRequest(name, Euro(euros), TargetDate: target));

    private static PaymentOption Option(Affordability a, PaymentMethod method) => a.Options.Single(o => o.Method == method);

    // ===== Affordability (spec §14, §23: obligations, buffer, VISA settlement) =====

    [Fact]
    public async Task A_purchase_that_keeps_the_buffer_through_the_rent_is_prudent()
    {
        await SetUpAsync();
        var a = await _affordability.CheckAsync(Euro(500));

        Assert.Equal(AffordabilityVerdict.Prudent, a.Verdict);
        Assert.Equal(Euro(2_000), a.CashNow);
        Assert.Equal(Euro(1_500), a.CashAfter);

        // The lowest point is right after the rent, before the salary arrives.
        Assert.Equal((Euro(600), new DateOnly(2026, 10, 15)), (a.LowestAfter, a.LowestOn));
        Assert.True(a.BufferIntact);
        Assert.True(a.BillsCovered);
        Assert.Equal(new DateOnly(2026, 10, 25), a.NextIncome);
        Assert.Equal(a.Today, a.EarliestPrudent);
        Assert.Empty(a.Caveats);
    }

    [Fact]
    public async Task Technically_affordable_is_not_called_prudent_when_the_buffer_breaks()
    {
        await SetUpAsync();

        var tight = await _affordability.CheckAsync(Euro(700));
        Assert.Equal(AffordabilityVerdict.TechnicallyOnly, tight.Verdict);
        Assert.False(tight.BufferIntact);
        Assert.True(tight.BillsCovered);
        Assert.Equal(new DateOnly(2026, 10, 25), tight.EarliestPrudent);

        var billsAtRisk = await _affordability.CheckAsync(Euro(1_500));
        Assert.Equal(AffordabilityVerdict.TechnicallyOnly, billsAtRisk.Verdict);
        Assert.False(billsAtRisk.BillsCovered);

        var tooMuch = await _affordability.CheckAsync(Euro(2_500));
        Assert.Equal(AffordabilityVerdict.NotAffordable, tooMuch.Verdict);
    }

    [Fact]
    public async Task Paying_by_card_moves_the_money_to_the_statement_debit_after_the_salary()
    {
        var (_, visa) = await SetUpAsync();
        var a = await _affordability.CheckAsync(Euro(700));

        var card = Option(a, PaymentMethod.CreditCard);
        Assert.Equal(visa.Id, card.Card!.Id);
        Assert.Equal(AffordabilityVerdict.Prudent, card.Verdict);
        Assert.Equal(new DateOnly(2026, 11, 5), card.PaidOn);
        Assert.Equal(Euro(400), card.AvailableCreditAfter);
        Assert.Equal(Euro(1_100), card.StatementAfter);
        Assert.Equal(Euro(1_100), card.LowestBalance);

        Assert.Equal(AffordabilityVerdict.TechnicallyOnly, Option(a, PaymentMethod.CurrentAccount).Verdict);
        var later = Option(a, PaymentMethod.Postpone);
        Assert.Equal((AffordabilityVerdict.Prudent, new DateOnly(2026, 10, 25)), (later.Verdict, later.PaidOn));

        // Over the available credit the card is not an option, whatever the bank balance.
        Assert.Equal(PaymentNote.OverLimit, Option(await _affordability.CheckAsync(Euro(1_200)), PaymentMethod.CreditCard).Note);
    }

    [Fact]
    public async Task Without_planned_income_the_answer_says_so_and_finds_no_later_date()
    {
        await SetUpAsync(salary: false);
        var a = await _affordability.CheckAsync(Euro(700));

        Assert.Contains(ForecastCaveat.NoIncomePlanned, a.Caveats);
        Assert.Null(a.EarliestPrudent);
        Assert.Equal(PaymentNote.NoDateFound, Option(a, PaymentMethod.Postpone).Note);
    }

    [Fact]
    public async Task Without_spendable_accounts_nothing_is_guessed()
    {
        var a = await _affordability.CheckAsync(Euro(100));
        Assert.False(a.CanCalculate);
        Assert.Equal(AffordabilityVerdict.CannotCalculate, a.Verdict);
    }

    [Fact]
    public async Task Buying_one_item_shows_how_it_delays_the_others()
    {
        await SetUpAsync();
        var tv = await ItemAsync("Televisie", 700);
        var bike = await ItemAsync("Fiets", 1_500, target: new(2026, 11, 1));
        var others = (await _wishlist.ListAsync()).Select(e => e.Item).ToList();

        var a = await _affordability.CheckAsync(tv.ExpectedPrice, tv, others);

        var effect = Assert.Single(a.Effects);
        Assert.Equal(bike.Id, effect.Item.Id);
        Assert.Equal((new DateOnly(2026, 10, 25), new DateOnly(2026, 11, 25)), (effect.Before, effect.After));
        Assert.True(effect.IsDelayed);

        var bikeCheck = await _affordability.CheckAsync(bike.ExpectedPrice, bike, others);
        Assert.Equal(PaymentNote.BeforeTargetDate, Option(bikeCheck, PaymentMethod.Postpone).Note);

        var summaries = await _affordability.SummariesAsync(others);
        Assert.Equal(AffordabilityVerdict.TechnicallyOnly, summaries[tv.Id].Verdict);
        Assert.Equal(new DateOnly(2026, 10, 25), summaries[bike.Id].EarliestPrudent);
    }

    // ===== Buying (spec §14: one transaction, never duplicated; §23: no ledger change without confirmation) =====

    [Fact]
    public async Task Buying_records_one_expense_and_cannot_be_repeated()
    {
        var (current, _) = await SetUpAsync();
        var tv = await ItemAsync("Televisie", 700);
        Assert.Equal(1, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));

        var created = await _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 10), Euro(689), current.Id));

        Assert.NotNull(created);
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(Euro(1_311), await _ledger.GetBalanceAsync(current.Id));
        var entry = Assert.Single(await _wishlist.ListAsync());
        Assert.Equal((WishlistStatus.Purchased, created!.Id), (entry.Status, entry.Item.PurchaseTransactionId));
        Assert.Equal("Televisie", created.Description);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 10), Euro(689), current.Id)));
        Assert.Equal("wishlist.alreadyPurchased", error.Code);
        error = await Assert.ThrowsAsync<DomainValidationException>(() => _wishlist.SetStatusAsync(tv.Id, WishlistStatus.Desired));
        Assert.Equal("wishlist.alreadyPurchased", error.Code);
        Assert.Equal(2, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public async Task Undoing_the_purchase_opens_the_item_again()
    {
        var (_, visa) = await SetUpAsync();
        var tv = await ItemAsync("Televisie", 700);
        await _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 10), Euro(700), visa.Id));
        Assert.Equal(WishlistStatus.Purchased, (await _wishlist.ListAsync())[0].Status);

        Assert.True(await _ledger.UndoAsync());

        var entry = (await _wishlist.ListAsync())[0];
        Assert.True(entry.PurchaseRemoved);
        Assert.Equal(WishlistStatus.Desired, entry.Status);
        Assert.True(entry.IsOpen);

        var again = await _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 10), Euro(650), visa.Id));
        Assert.Equal(again!.Id, (await _wishlist.ListAsync())[0].Item.PurchaseTransactionId);
    }

    [Fact]
    public async Task Marking_as_bought_without_an_expense_changes_no_balance_and_can_be_reopened()
    {
        var (current, _) = await SetUpAsync();
        var tv = await ItemAsync("Televisie", 700);

        Assert.Null(await _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 9), Euro(700), null)));
        Assert.Equal(1, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        Assert.Equal(Euro(2_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Equal(WishlistStatus.Purchased, (await _wishlist.ListAsync())[0].Status);
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 10, 9), Euro(700), current.Id)));
        Assert.Equal("wishlist.alreadyPurchased", error.Code);

        await _wishlist.SetStatusAsync(tv.Id, WishlistStatus.Desired);
        Assert.Equal(WishlistStatus.Desired, (await _wishlist.ListAsync())[0].Status);
    }

    [Fact]
    public async Task An_invalid_purchase_saves_nothing()
    {
        var (current, _) = await SetUpAsync();
        var tv = await ItemAsync("Televisie", 700);

        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _wishlist.PurchaseAsync(tv.Id, new PurchaseRequest(new(2026, 8, 1), Euro(700), current.Id)));
        Assert.Equal("transaction.date.beforeOpening", error.Code);
        Assert.Equal(WishlistStatus.Desired, (await _wishlist.ListAsync())[0].Status);
        Assert.Equal(1, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
    }

    [Fact]
    public async Task Items_are_validated_and_listed_open_first_by_priority()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => _wishlist.CreateAsync(new WishlistRequest("Gratis", Money.Zero)));
        Assert.Equal("wishlist.price.notPositive", error.Code);

        var low = await _wishlist.CreateAsync(new WishlistRequest("Lamp", Euro(40), WishlistPriority.Low));
        var high = await _wishlist.CreateAsync(new WishlistRequest("Bureaustoel", Euro(300), WishlistPriority.High, MonthlySaving: Euro(50), Url: "https://example.org/stoel"));
        var cancelled = await _wishlist.CreateAsync(new WishlistRequest("Drone", Euro(900), WishlistPriority.High));
        await _wishlist.SetStatusAsync(cancelled.Id, WishlistStatus.Cancelled);

        var list = await _wishlist.ListAsync();
        Assert.Equal([high.Id, low.Id, cancelled.Id], list.Select(e => e.Item.Id));
        Assert.Equal(Euro(50), list[0].Item.MonthlySaving);
        Assert.Equal("https://example.org/stoel", list[0].Item.Url);
        Assert.False(list[2].IsOpen);
    }
}
