using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;
using PersonalFinanceOS.Infrastructure.Persistence;

namespace PersonalFinanceOS.Tests.Infrastructure;

/// <summary>
/// D-039 against a real migrated database: fixed costs, income and usual spending are learned from the history, and
/// safe-to-spend uses them without any budget. The fixture: history since 1 April, today 10 October, six complete months.
/// </summary>
public sealed class SpendingProfileTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 4, 1);

    private readonly TempDataFolder _temp = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.FromHours(2)));
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly RecurringService _recurring;
    private readonly AppSettingsService _settings;
    private readonly SpendingProfileService _profile;
    private readonly ForecastService _forecast;

    public SpendingProfileTests()
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
        _profile = new SpendingProfileService(accountRepository, categoryRepository, transactions, new CreditCardRepository(db), _recurring, _clock);
        _forecast = new ForecastService(accountRepository, _ledger, _recurring, cards, _settings, _profile, _clock);
    }

    public void Dispose() => _temp.Dispose();

    private static Money Euro(decimal euros) => Money.FromEuros(euros);

    private sealed record Fixture(Account Current, Account Savings, Account Joint, Account Visa);

    /// <summary>
    /// Every month: salary (a slightly different amount), rent with the month in its description, Netflix on the VISA,
    /// €200 to savings, €500 to the joint account, energy (planned by the user) and four grocery runs of €300 together.
    /// Now and then: a pizzeria, a €1,200 television in June, and Spotify until July. October so far: rent and €140 groceries.
    /// </summary>
    private async Task<Fixture> SetUpAsync()
    {
        var current = await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, Opening, Euro(3_000), Share.Full));
        var savings = await _accounts.CreateAsync(new CreateAccountRequest("Spaarrekening", AccountType.Savings, Opening, Money.Zero, Share.Full));
        var joint = await _accounts.CreateAsync(new CreateAccountRequest("Gezamenlijk", AccountType.JointCurrent, Opening, Money.Zero, Share.FromPercent(50)));
        var visa = await _accounts.CreateAsync(new CreateAccountRequest(
            "VISA", AccountType.CreditCard, Opening, Money.Zero, Share.Full, Card: new CardSettings(Euro(1_500), 1, 5, current.Id)));

        string[] months = ["april", "mei", "juni", "juli", "augustus", "september"];
        decimal[] salary = [2_500, 2_550, 2_480, 2_500, 2_520, 2_500];
        for (var i = 0; i < 6; i++)
        {
            var m = Opening.AddMonths(i);
            await Spend(current, m, 850, $"Verhuurder Peeters huur {months[i]} ref {1000 + i}");
            await _ledger.RecordAsync(new TransactionRequest(current.Id, m.AddDays(24), Euro(salary[i]), TransactionType.Income, $"ACME NV loon {m.Month:00}/2026"));
            await Spend(visa, m.AddDays(13), 17.99m, $"NETFLIX.COM {4000 + i}");
            await _ledger.RecordTransferAsync(new TransferRequest(current.Id, savings.Id, m.AddDays(25), Euro(200), "Sparen"));
            await _ledger.RecordTransferAsync(new TransferRequest(current.Id, joint.Id, m.AddDays(1), Euro(500), "Naar gezamenlijk"));
            await Spend(current, m.AddDays(4), 120, "Engie");
            foreach (var (day, amount) in new[] { (3, 60m), (10, 75m), (17, 80m), (24, 85m) })
            {
                await Spend(current, m.AddDays(day), amount, "Colruyt Gent");
            }

            if (i is 0 or 2 or 4)
            {
                await Spend(current, m.AddDays(19), 50, "Pizzeria");
            }

            if (i < 4)
            {
                await Spend(visa, m.AddDays(7), 10.99m, "Spotify");
            }

            await _ledger.RecordTransferAsync(new TransferRequest(current.Id, visa.Id, m.AddDays(4), i < 4 ? Euro(28.98m) : Euro(17.99m), "VISA"));
        }

        await Spend(current, new(2026, 6, 12), 1_200, "MediaMarkt");
        await Spend(current, new(2026, 10, 1), 850, "Verhuurder Peeters huur oktober ref 1006");
        await Spend(current, new(2026, 10, 3), 60, "Colruyt Gent");
        await Spend(current, new(2026, 10, 8), 80, "Colruyt Gent");

        // Energy is the one thing the user planned; the plan counts, not a second recognised copy.
        await _recurring.CreateRuleAsync(new RecurringRuleRequest("Energie", RecurringKind.Expense, current.Id, null, null, Euro(120), RecurrenceFrequency.Monthly, new(2026, 10, 5)));
        return new Fixture(current, savings, joint, visa);
    }

    private Task<Transaction> Spend(Account account, DateOnly date, decimal euros, string description) =>
        _ledger.RecordAsync(new TransactionRequest(account.Id, date, Euro(euros), TransactionType.Expense, description));

    [Fact]
    public async Task Fixed_costs_income_and_transfers_are_recognised_and_the_rest_is_usual_spending()
    {
        var f = await SetUpAsync();
        var p = await _profile.GetAsync();

        Assert.True(p.IsKnown);
        Assert.Equal((6, new DateOnly(2026, 4, 1), new DateOnly(2026, 9, 30)), (p.Months, p.From, p.To));
        Assert.Equal(f.Current.Id, p.SpendingAccountId);

        // Rent, Netflix on the card, both standing transfers and the salary. Not the groceries (four times a month), the
        // pizzeria (now and then), the television (once), Spotify (stopped in July) or energy (already planned).
        var found = p.FixedCosts.ToDictionary(c => c.Name);
        Assert.Equal(
            ["ACME NV loon 09/2026", "NETFLIX.COM 4005", "Naar gezamenlijk", "Sparen", "Verhuurder Peeters huur oktober ref 1006"],
            found.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((RecurringKind.Expense, f.Current.Id, Euro(850), new DateOnly(2026, 10, 1)), Shape(found["Verhuurder Peeters huur oktober ref 1006"]));
        Assert.Equal((RecurringKind.Expense, f.Visa.Id, Euro(17.99m), new DateOnly(2026, 9, 14)), Shape(found["NETFLIX.COM 4005"]));
        Assert.Equal((RecurringKind.Transfer, f.Current.Id, Euro(200), new DateOnly(2026, 9, 26)), Shape(found["Sparen"]));
        Assert.Equal(f.Savings.Id, found["Sparen"].ToAccountId);
        Assert.Equal(f.Joint.Id, found["Naar gezamenlijk"].ToAccountId);

        // Income counts at the lower of its last and usual amount: never more than usually comes in.
        Assert.Equal((RecurringKind.Income, f.Current.Id, Euro(2_500), new DateOnly(2026, 9, 25)), Shape(found["ACME NV loon 09/2026"]));

        // Day-to-day per month: 360.99, 310.99, 1,560.99 (television), 310.99, 350, 300. The middle month, not the average.
        Assert.Equal(Euro(330.50m), p.UsualMonthly);
        Assert.Equal(Euro(140), p.SpentThisMonth);

        // €190.50 of the usual month is left, but the usual pace for the 21 days left (€223.89) is never undercut.
        Assert.Equal(Euro(223.89m), p.RestOfMonth);
        Assert.Equal(p.RestOfMonth, p.UsualSpendingUntil(new DateOnly(2026, 10, 31)));
        Assert.Equal(p.RestOfMonth + p.UsualMonthly, p.UsualSpendingUntil(new DateOnly(2026, 11, 30)));
    }

    [Fact]
    public async Task Recognised_items_are_expected_a_month_after_they_were_last_seen()
    {
        await SetUpAsync();
        var p = await _profile.GetAsync();
        var today = new DateOnly(2026, 10, 10);
        DateOnly[] Due(string name) => p.FixedCosts.Single(c => c.Name == name).DueDates(today, new(2026, 11, 30)).ToArray();

        Assert.Equal([new(2026, 11, 1)], Due("Verhuurder Peeters huur oktober ref 1006"));
        Assert.Equal([new(2026, 10, 14), new(2026, 11, 14)], Due("NETFLIX.COM 4005"));
        Assert.Equal([new(2026, 10, 25), new(2026, 11, 25)], Due("ACME NV loon 09/2026"));

        // The joint transfer was due on 2 October and is not seen yet: a cost still counts, today.
        Assert.Equal([today, new(2026, 11, 2)], Due("Naar gezamenlijk"));
    }

    [Fact]
    public async Task Safe_to_spend_uses_what_was_learned_without_budgets_or_planned_income()
    {
        await SetUpAsync();
        await _settings.SetMinimumBufferAsync(Euro(250));
        var safe = await _forecast.SafeToSpendAsync();

        // Payday is recognised from the history: the day before the salary that usually comes on the 25th.
        Assert.Equal(new DateOnly(2026, 10, 24), safe.Until);
        Assert.Equal(("ACME NV loon 09/2026", new DateOnly(2026, 10, 25), true), (safe.NextIncome!.Name, safe.NextIncome.Date, safe.NextIncome.Detected));
        Assert.DoesNotContain(ForecastCaveat.NoIncomePlanned, safe.Caveats);
        Assert.DoesNotContain(ForecastCaveat.NoSpendingHistory, safe.Caveats);

        Assert.Contains(safe.Lines, l => l is { Kind: ForecastLineKind.Planned, Detected: true, Name: "Naar gezamenlijk" } && l.Amount == Euro(-500) && l.Date == new DateOnly(2026, 10, 10));
        Assert.Contains(safe.Lines, l => l is { Kind: ForecastLineKind.Planned, Detected: true, Name: "NETFLIX.COM 4005" } && l.Amount == Euro(-17.99m));
        Assert.Contains(safe.Lines, l => l is { Kind: ForecastLineKind.Planned, Detected: false, Name: "Energie" });
        Assert.DoesNotContain(safe.Lines, l => l.Detected && l.Name == "Engie");

        // 14 of the 21 days left in October, at €223.89 for those 21 days.
        Assert.Equal(Euro(-149.26m), safe.Lines.Single(l => l.Kind == ForecastLineKind.UsualSpending).Amount);
        Assert.Equal(Money.Sum(safe.Lines.Select(l => l.Amount)), safe.Amount);
    }

    [Fact]
    public async Task The_timeline_spreads_usual_spending_over_the_days_and_keeps_transfers_on_both_sides()
    {
        var f = await SetUpAsync();
        var timeline = await _forecast.TimelineAsync(new DateOnly(2026, 11, 30));

        var usual = timeline.Events.Where(e => e.Kind == CashEventKind.Usual).ToList();
        Assert.All(usual, e => Assert.Equal(f.Current.Id, e.AccountId));
        Assert.Equal(new DateOnly(2026, 10, 11), usual[0].Date);
        Assert.Equal(Euro(-(223.89m + 330.50m)), Money.Sum(usual.Select(e => e.Amount)));

        Assert.Contains(timeline.Events, e => e is { Kind: CashEventKind.PlannedIn, Detected: true } && e.AccountId == f.Savings.Id && e.Date == new DateOnly(2026, 10, 26));
        Assert.Contains(timeline.Events, e => e is { Kind: CashEventKind.Income, Detected: true } && e.Date == new DateOnly(2026, 11, 25));
    }

    [Fact]
    public async Task Too_little_history_learns_nothing_and_says_so()
    {
        await _accounts.CreateAsync(new CreateAccountRequest("Zichtrekening", AccountType.PersonalCurrent, new(2026, 8, 15), Euro(1_000), Share.Full));
        var p = await _profile.GetAsync();

        Assert.False(p.IsKnown);
        Assert.Empty(p.FixedCosts);
        Assert.Empty(p.UsualSpending(new DateOnly(2026, 12, 31)));
        var safe = await _forecast.SafeToSpendAsync();
        Assert.Contains(ForecastCaveat.NoSpendingHistory, safe.Caveats);
        Assert.Equal(Euro(1_000), safe.Amount);
    }

    [Theory]
    [InlineData("NETFLIX.COM 4005", "NETFLIX.COM 4012", true)]
    [InlineData("Verhuurder Peeters huur april ref 1000", "Verhuurder Peeters huur september ref 1005", true)]
    [InlineData("Betaling debetkaart 4871 04XX XXXX 1234 Colruyt 12-10", "Betaling debetkaart 4871 04XX XXXX 1234 Colruyt 13-11", true)]
    [InlineData("Colruyt Gent", "Delhaize Gent", false)]
    public void The_counterparty_key_ignores_numbers_month_names_and_masked_cards(string a, string b, bool same)
    {
        var now = DateTimeOffset.UnixEpoch;
        var first = Transaction.Create(Guid.NewGuid(), new(2026, 4, 1), Euro(10), TransactionType.Expense, a, null, null, TransactionSource.Import, now);
        var second = Transaction.Create(Guid.NewGuid(), new(2026, 5, 1), Euro(10), TransactionType.Expense, b, null, null, TransactionSource.Import, now);
        Assert.Equal(same, SpendingProfileService.Key(first) == SpendingProfileService.Key(second));
    }

    private static (RecurringKind, Guid, Money, DateOnly) Shape(DetectedFixedCost c) => (c.Kind, c.AccountId, c.Amount, c.LastSeen);
}
