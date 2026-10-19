using System.Text;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Import;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>Phase 4 against a real migrated database: VISA (spec §10), rounding (§11), recurring items (§12), safe-to-spend (§13) and the matching acceptance tests of §23.</summary>
public sealed class PlanningTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 9, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly CreditCardService _cards;
    private readonly ForecastService _forecast;
    private readonly AppSettingsService _settings;
    private readonly ImportService _import;

    public PlanningTests()
    {
        Assert.True(_temp.Initializer().Initialize().CanOpen);
        var options = FinanceDbContext.OptionsFor(_temp.Paths.DatabaseFile);
        Func<FinanceDbContext> db = () => new FinanceDbContext(options);
        var audit = new AuditLog(db, _clock);
        var store = new SettingsStore(db, _clock);
        var accountRepository = new AccountRepository(db);
        var categoryRepository = new CategoryRepository(db);
        var transactions = new TransactionRepository(db);
        _settings = new AppSettingsService(store);
        _accounts = new AccountService(accountRepository, audit, _clock);
        _ledger = new LedgerService(transactions, accountRepository, categoryRepository, audit, new UndoHistory(), _clock);
        _recurring = new RecurringService(new RecurringRepository(db), accountRepository, categoryRepository, transactions, _ledger, audit, _clock);
        _cards = new CreditCardService(new CreditCardRepository(db), accountRepository, transactions, _recurring, audit, _clock);
        _forecast = new ForecastService(accountRepository, _ledger, _recurring, _cards, _settings, new SpendingProfileService(accountRepository, categoryRepository, transactions, new CreditCardRepository(db), _recurring, _clock), _clock);
        _import = new ImportService(new ImportRepository(db), transactions, accountRepository, categoryRepository, new PayeeRuleRepository(db), audit, _ledger, _settings, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private DateOnly Today => ((IClock)_clock).Today;

    private Task<Account> AccountAsync(string name, AccountType type, long openingCents = 0) =>
        _accounts.CreateAsync(new CreateAccountRequest(name, type, Opening, Money.FromCents(openingCents), Share.Full));

    private async Task<(Account Current, Account Visa)> CardAsync(int startDay = 28, int? settlementDay = 5, long limitCents = 150_000)
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Money.FromCents(limitCents), startDay, settlementDay, current.Id)));
        return (current, visa);
    }

    private Task<Transaction> PurchaseAsync(Account card, DateOnly date, long cents, string text = "Winkel") =>
        _ledger.RecordAsync(new TransactionRequest(card.Id, date, Money.FromCents(cents), TransactionType.Expense, text));

    private Task<(Transaction From, Transaction To)> PayCardAsync(Account from, Account card, DateOnly date, long cents) =>
        _ledger.RecordTransferAsync(new TransferRequest(from.Id, card.Id, date, Money.FromCents(cents), "VISA afrekening"));

    private async Task<CardStatus> StatusAsync(Account card) => (await _cards.ListStatusesAsync()).Single(s => s.Card.Id == card.Id);

    private Task<RecurringRule> RuleAsync(string name, RecurringKind kind, Account account, long cents, DateOnly start, Account? to = null) =>
        _recurring.CreateRuleAsync(new RecurringRuleRequest(name, kind, account.Id, to?.Id, null, Money.FromCents(cents), RecurrenceFrequency.Monthly, start));

    // ===== VISA (spec §10, §23) =====

    [Fact]
    public async Task A_card_needs_its_settings_and_they_can_be_changed_later()
    {
        var error = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _accounts.CreateAsync(new CreateAccountRequest("VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full)));
        Assert.Equal("card.settings.required", error.Code);
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM accounts;"));

        var (current, visa) = await CardAsync(startDay: 15, settlementDay: null, limitCents: 100_000);
        var status = await StatusAsync(visa);
        Assert.Equal(new DateOnly(2026, 9, 15), status.CurrentPeriod.Start);
        Assert.Equal(Money.FromCents(100_000), status.AvailableCredit);

        await _cards.SaveSettingsAsync(visa.Id, new CardSettings(Money.FromCents(250_000), 28, 5, current.Id));
        status = await StatusAsync(visa);
        Assert.Equal(new DateOnly(2026, 9, 28), status.CurrentPeriod.Start);
        Assert.Equal(Money.FromCents(250_000), status.AvailableCredit); // the limit is configurable

        var wrongPayer = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _cards.SaveSettingsAsync(visa.Id, new CardSettings(Money.FromCents(250_000), 28, 5, visa.Id)));
        Assert.Equal("card.settlementAccount.invalid", wrongPayer.Code);
    }

    [Fact]
    public async Task A_purchase_and_its_settlement_count_once_and_move_the_right_balances()
    {
        var (current, visa) = await CardAsync();

        await PurchaseAsync(visa, new(2026, 10, 2), 40_000);
        Assert.Equal(Money.FromCents(200_000), await _ledger.GetBalanceAsync(current.Id)); // not paid yet
        var status = await StatusAsync(visa);
        Assert.Equal(Money.FromCents(40_000), status.Outstanding);
        Assert.Equal(Money.FromCents(110_000), status.AvailableCredit);
        Assert.Equal(Money.FromCents(40_000), status.CurrentSpending);
        Assert.Equal(Money.FromCents(40_000), status.EstimatedStatement);
        Assert.Equal((new DateOnly(2026, 11, 5), Money.FromCents(40_000)), (status.NextSettlement!.Date!.Value, status.NextSettlement.Amount));

        // After the period closes the bank debits the current account.
        _clock.Now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(1));
        await PayCardAsync(current, visa, new(2026, 11, 5), 40_000);

        Assert.Equal(Money.FromCents(160_000), await _ledger.GetBalanceAsync(current.Id));
        status = await StatusAsync(visa);
        Assert.Equal(Money.Zero, status.Outstanding);
        Assert.Equal(Money.FromCents(40_000), status.LastStatement);
        Assert.Null(status.SettlementDifference);
        Assert.Null(status.NextSettlement);
        Assert.Equal(Money.FromCents(40_000), (await _ledger.GetTotalsAsync(new(2026, 10, 1), new(2026, 11, 30))).Expenses); // no second expense
    }

    [Fact]
    public async Task A_settlement_that_differs_from_the_estimate_is_surfaced_not_corrected()
    {
        var (current, visa) = await CardAsync();
        await PurchaseAsync(visa, new(2026, 10, 2), 40_000);
        _clock.Now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(1));
        await PayCardAsync(current, visa, new(2026, 11, 5), 35_000);

        var status = await StatusAsync(visa);
        Assert.Equal(Money.FromCents(-5_000), status.SettlementDifference);
        Assert.Equal(Money.FromCents(5_000), status.Outstanding); // nothing was adjusted silently
        Assert.Equal(Money.FromCents(5_000), status.NextSettlement!.Amount);
    }

    [Fact]
    public async Task Card_subscriptions_belong_to_the_statement_period_of_their_date()
    {
        var (_, visa) = await CardAsync();
        await PurchaseAsync(visa, new(2026, 10, 2), 10_000);
        await RuleAsync("Streaming", RecurringKind.Expense, visa, 1_299, new(2026, 10, 27));

        var status = await StatusAsync(visa);
        Assert.Equal(Money.FromCents(1_299), status.PlannedInPeriod); // the 27th still belongs to this period
        Assert.Equal(Money.FromCents(11_299), status.EstimatedStatement);

        var settlements = await _cards.ListSettlementsAsync(new(2027, 1, 10));
        Assert.Equal(
            [(new DateOnly(2026, 11, 5), 11_299L), (new DateOnly(2026, 12, 5), 1_299L), (new DateOnly(2027, 1, 5), 1_299L)],
            settlements.Select(s => (s.Date!.Value, s.Amount.Cents)).ToArray());
    }

    // ===== Recurring items (spec §12, §23) =====

    [Fact]
    public async Task Planned_items_change_forecasts_but_never_balances()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        await RuleAsync("Huur", RecurringKind.Expense, current, 85_000, new(2026, 10, 15));

        Assert.Equal(Money.FromCents(200_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Equal(0, _temp.Scalar("SELECT COUNT(*) FROM transactions;"));
        var safe = await _forecast.SafeToSpendAsync();
        Assert.Equal(Money.FromCents(115_000), safe.Amount);
    }

    [Fact]
    public async Task Confirming_creates_one_linked_transaction_and_undo_reopens_the_item()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        var rent = await RuleAsync("Huur", RecurringKind.Expense, current, 85_000, new(2026, 10, 1));

        var open = await _recurring.ListOpenAsync(new(2026, 10, 31));
        var item = Assert.Single(open);
        Assert.True(item.IsOverdue);

        var created = await _recurring.ConfirmAsync(rent.Id, item.OccurrenceDate, new(2026, 10, 2), Money.FromCents(86_000));
        var line = Assert.Single(created);
        Assert.Equal((TransactionSource.Scheduled, new DateOnly(2026, 10, 2), Money.FromCents(-86_000)), (line.Source, line.Date, line.Amount));
        Assert.Equal(Money.FromCents(114_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Empty(await _recurring.ListOpenAsync(new(2026, 10, 31)));

        var twice = await Assert.ThrowsAsync<DomainValidationException>(() =>
            _recurring.ConfirmAsync(rent.Id, item.OccurrenceDate, new(2026, 10, 2), Money.FromCents(86_000)));
        Assert.Equal("scheduled.alreadyHandled", twice.Code);

        Assert.True(await _ledger.UndoAsync());
        Assert.Equal(Money.FromCents(200_000), await _ledger.GetBalanceAsync(current.Id));
        Assert.Single(await _recurring.ListOpenAsync(new(2026, 10, 31))); // expected again
    }

    [Fact]
    public async Task An_imported_line_is_suggested_and_linked_instead_of_counted_twice()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        var rent = await RuleAsync("Huur", RecurringKind.Expense, current, 85_000, new(2026, 10, 1));
        var energy = await RuleAsync("Energie", RecurringKind.Expense, current, 12_000, new(2026, 10, 3));
        var bank = await _ledger.RecordAsync(new TransactionRequest(current.Id, new(2026, 10, 3), Money.FromCents(84_950), TransactionType.Expense, "HUUR OKTOBER"));
        var bill = await _ledger.RecordAsync(new TransactionRequest(current.Id, new(2026, 10, 4), Money.FromCents(12_640), TransactionType.Expense, "ENGIE"));

        var open = await _recurring.ListOpenAsync(new(2026, 10, 31));
        Assert.Equal(bank.Id, open.Single(o => o.Rule.Id == rent.Id).SuggestedMatch!.Id);
        Assert.Equal(bill.Id, open.Single(o => o.Rule.Id == energy.Id).SuggestedMatch!.Id); // within 10%, each line used once

        await _recurring.LinkAsync(rent.Id, new(2026, 10, 1), bank.Id);
        Assert.Equal(Money.FromCents(200_000 - 84_950 - 12_640), await _ledger.GetBalanceAsync(current.Id));
        Assert.DoesNotContain(await _recurring.ListOpenAsync(new(2026, 10, 31)), o => o.Rule.Id == rent.Id);

        var taken = await Assert.ThrowsAsync<DomainValidationException>(() => _recurring.LinkAsync(energy.Id, new(2026, 10, 3), bank.Id));
        Assert.Equal("scheduled.link.taken", taken.Code);
    }

    [Fact]
    public async Task Postponed_items_move_skipped_items_disappear_and_both_can_be_reopened()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 200_000);
        var rule = await RuleAsync("Verzekering", RecurringKind.Expense, current, 30_000, new(2026, 10, 12));

        await _recurring.PostponeAsync(rule.Id, new(2026, 10, 12), new(2026, 10, 20));
        var moved = Assert.Single(await _recurring.ListOpenAsync(new(2026, 10, 31)));
        Assert.Equal((new DateOnly(2026, 10, 20), true), (moved.DueDate, moved.IsPostponed));

        await _recurring.SkipAsync(rule.Id, new(2026, 10, 12));
        Assert.Empty(await _recurring.ListOpenAsync(new(2026, 10, 31)));

        await _recurring.ReopenAsync(rule.Id, new(2026, 10, 12));
        Assert.Equal(new DateOnly(2026, 10, 12), Assert.Single(await _recurring.ListOpenAsync(new(2026, 10, 31))).DueDate);
    }

    // ===== Safe-to-spend and cash flow (spec §13) =====

    [Fact]
    public async Task Safe_to_spend_runs_until_the_day_before_expected_income()
    {
        var (current, visa) = await CardAsync(settlementDay: 15);
        var savings = await AccountAsync("Spaarrekening", AccountType.Savings);
        await PurchaseAsync(visa, new(2026, 9, 20), 30_000);   // closed period: debited 15 October
        await PurchaseAsync(visa, new(2026, 10, 2), 40_000);   // current period: debited 15 November
        await RuleAsync("Loon", RecurringKind.Income, current, 240_000, new(2026, 10, 25));
        await RuleAsync("Huur", RecurringKind.Expense, current, 85_000, new(2026, 10, 15));
        await RuleAsync("Sparen", RecurringKind.Transfer, current, 20_000, new(2026, 10, 20), savings);
        await _settings.SetMinimumBufferAsync(Money.FromCents(10_000));

        var safe = await _forecast.SafeToSpendAsync();

        Assert.True(safe.CanCalculate);
        Assert.Equal(new DateOnly(2026, 10, 24), safe.Until);
        Assert.Equal("Loon", safe.NextIncome!.Name);
        Assert.Equal(Money.FromCents(200_000 - 85_000 - 20_000 - 30_000 - 10_000), safe.Amount);
        Assert.Contains(safe.Lines, l => l.Kind == ForecastLineKind.CardSettlement && l.Amount == Money.FromCents(-30_000) && l.Date == new DateOnly(2026, 10, 15));

        // Without two complete months of history there is no usual spending to count yet, and the forecast says so.
        Assert.Equal([ForecastCaveat.NoSpendingHistory], safe.Caveats);
    }

    [Fact]
    public async Task Without_planned_income_safe_to_spend_says_so_and_uses_a_fixed_horizon()
    {
        await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 50_000);
        await _accounts.CreateAsync(new CreateAccountRequest("VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Money.FromCents(150_000), 28, null, null)));

        var safe = await _forecast.SafeToSpendAsync();
        Assert.Equal(Today.AddDays(ForecastService.DefaultHorizonDays), safe.Until);
        Assert.Null(safe.NextIncome);
        Assert.Contains(ForecastCaveat.NoIncomePlanned, safe.Caveats);
        Assert.Equal(Money.FromCents(50_000), safe.Amount);
    }

    [Fact]
    public async Task Cash_flow_counts_expected_income_and_outflows_per_horizon()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 100_000);
        await RuleAsync("Loon", RecurringKind.Income, current, 240_000, new(2026, 10, 25));
        await RuleAsync("Huur", RecurringKind.Expense, current, 85_000, new(2026, 10, 15));

        var flows = await _forecast.CashFlowAsync([7, 30, 90]);
        Assert.Equal((0L, 85_000L), (flows[0].ExpectedIn.Cents, flows[0].ExpectedOut.Cents));
        Assert.Equal((240_000L, 85_000L), (flows[1].ExpectedIn.Cents, flows[1].ExpectedOut.Cents));
        Assert.Equal((3 * 240_000L, 3 * 85_000L), (flows[2].ExpectedIn.Cents, flows[2].ExpectedOut.Cents));
    }

    // ===== Bank rounding (spec §11, §23) =====

    private const string Header = "Datum;Omschrijving;Naam tegenpartij;Bedrag;Referentie";

    private static ImportFile File(params string[] lines) =>
        ImportFile.Read("rekening.csv", Encoding.UTF8.GetBytes(Header + "\n" + string.Join("\n", lines) + "\n"));

    private async Task<ImportPreview> PreviewAsync(Account account, ImportFile file) =>
        await _import.PreviewAsync(account.Id, file, (await _import.SuggestMappingAsync(file)).Mapping);

    [Fact]
    public async Task Rounding_is_a_transfer_to_savings_and_is_not_imported_twice()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 10_000);
        var savings = await AccountAsync("Spaarpot", AccountType.Savings);

        var preview = await PreviewAsync(current, File(
            "05/10/2026;BETALING DEBETKAART;BAKKERIJ;-2,95;R1",
            "05/10/2026;AFRONDING SPAREN;;-0,05;R2"));
        var rounding = preview.Candidates[1];
        Assert.Equal((ImportSuggestion.Rounding, TransactionType.Transfer, savings.Id), (rounding.Suggestion, rounding.Type, rounding.TransferAccountId!.Value));
        await _import.ConfirmAsync(preview);

        var totals = await _ledger.GetTotalsAsync(new(2026, 10, 1), new(2026, 10, 31));
        Assert.Equal(Money.FromCents(295), totals.Expenses); // €2.95 spending, the €0.05 is not
        Assert.Equal(Money.FromCents(5), await _ledger.GetBalanceAsync(savings.Id));

        // The savings statement shows the same rounding: already in the app.
        var other = await PreviewAsync(savings, File("05/10/2026;AFRONDING;ZICHTREKENING;0,05;S1"));
        Assert.Equal(ImportRowStatus.Duplicate, Assert.Single(other.Candidates).Status);
    }

    [Fact]
    public async Task Unclear_rounding_is_flagged_for_review_and_left_unticked()
    {
        var current = await AccountAsync("Zichtrekening", AccountType.PersonalCurrent, 10_000);
        await AccountAsync("Spaarrekening 1", AccountType.Savings);
        var second = await AccountAsync("Spaarrekening 2", AccountType.Savings);

        var preview = await PreviewAsync(current, File("05/10/2026;AFRONDING SPAREN;;-0,05;R2"));
        var line = Assert.Single(preview.Candidates);
        Assert.Equal(ImportSuggestion.RoundingUncertain, line.Suggestion);
        Assert.False(line.Include);

        // Once the user says where rounding goes, it is recognised.
        await _settings.SetRoundingAccountIdAsync(second.Id);
        line = Assert.Single((await PreviewAsync(current, File("05/10/2026;AFRONDING SPAREN;;-0,05;R2"))).Candidates);
        Assert.Equal((ImportSuggestion.Rounding, second.Id), (line.Suggestion, line.TransferAccountId!.Value));
        Assert.True(line.Include);
    }
}
