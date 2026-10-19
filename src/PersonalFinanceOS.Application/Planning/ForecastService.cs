using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Planning;

namespace PersonalFinanceOS.Application.Planning;

/// <summary>What a line of the safe-to-spend calculation is.</summary>
public enum ForecastLineKind
{
    /// <summary>Balance of the counted accounts today.</summary>
    Balance,

    /// <summary>Transactions already recorded with a date after today, within the horizon.</summary>
    RecordedLater,

    /// <summary>A planned expense or transfer out (open occurrence).</summary>
    Planned,

    /// <summary>A planned transfer into the counted accounts.</summary>
    PlannedIn,

    /// <summary>A card statement paid from a counted account.</summary>
    CardSettlement,

    /// <summary>The minimum buffer that must stay on the account.</summary>
    Buffer,

    /// <summary>The usual day-to-day spending until the horizon, learned from the history (D-039).</summary>
    UsualSpending,
}

/// <summary>Why a forecast is less certain than it looks; screens show these next to the number.</summary>
public enum ForecastCaveat
{
    /// <summary>No expected income is planned, so a fixed horizon is used.</summary>
    NoIncomePlanned,

    /// <summary>A credit card has no settings yet, so its statement is not counted.</summary>
    CardNotSetUp,

    /// <summary>A card's debit day is unknown; its statement is counted as due right after the period.</summary>
    SettlementDateUnknown,

    /// <summary>A card is paid automatically but the paying account is not set.</summary>
    SettlementAccountUnknown,

    /// <summary>Planned items are past their date and not confirmed; they are still counted.</summary>
    OverdueItems,

    /// <summary>Less than two complete months of history, so usual spending and fixed costs are not known yet (D-039).</summary>
    NoSpendingHistory,
}

/// <summary>
/// One line of the safe-to-spend breakdown. <see cref="Amount"/> is signed: money out is negative. <see cref="Detected"/>
/// marks a fixed cost or income the app recognised in the history rather than one the user planned (D-039).
/// </summary>
public sealed record ForecastLine(ForecastLineKind Kind, Money Amount, string? Name = null, DateOnly? Date = null, bool Detected = false);

/// <summary>The next expected income: planned by the user, or recognised in the history. Expected, never guaranteed.</summary>
public sealed record ExpectedIncome(string Name, DateOnly Date, Money Amount, bool Detected);

/// <summary>
/// Safe-to-spend (spec §13): money on the counted accounts minus everything that is expected to leave
/// before the next income, minus the buffer. A forecast, not a balance: it never changes data.
/// </summary>
public sealed record SafeToSpend(
    bool CanCalculate,
    Money Amount,
    DateOnly Today,

    /// <summary>The last day counted: the day before the next expected income, or a fixed horizon.</summary>
    DateOnly Until,

    /// <summary>The next expected income, planned or recognised. Expected, never guaranteed.</summary>
    ExpectedIncome? NextIncome,
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<ForecastLine> Lines,
    IReadOnlyList<ForecastCaveat> Caveats,

    /// <summary>What the user's money usually does, which the usual spending and recognised items come from.</summary>
    SpendingProfile? Profile = null);

/// <summary>Expected money in and out over a number of days (spec §13: 7, 30 and 90 days).</summary>
public sealed record CashFlow(int Days, Money ExpectedIn, Money ExpectedOut)
{
    public Money Net => ExpectedIn - ExpectedOut;
}

/// <summary>
/// Forecasts from recorded transactions, open planned items, card statements and what the history shows (D-039): fixed
/// costs and income the app recognised by itself, and the usual day-to-day spending. Planned items affect forecasts
/// only; balances stay the ledger's (spec §12). Expected income is always labelled as expected and never counted before
/// it arrives in safe-to-spend. Budgets play no part.
/// </summary>
public sealed class ForecastService(
    IAccountRepository accounts,
    LedgerService ledger,
    RecurringService recurring,
    CreditCardService cards,
    AppSettingsService settings,
    SpendingProfileService spending,
    IClock clock)
{
    /// <summary>The horizon when no income is planned.</summary>
    public const int DefaultHorizonDays = 30;

    /// <summary>
    /// The accounts safe-to-spend counts: the personal current account(s) and cash. Joint accounts,
    /// savings and investments are not spendable by default, because money is never presumed to move
    /// between accounts unless it is planned (spec §13).
    /// </summary>
    public static bool Counts(Account account) =>
        account.Status == AccountStatus.Active && account.Type is AccountType.PersonalCurrent or AccountType.Cash;

    public async Task<SafeToSpend> SafeToSpendAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var all = await accounts.ListAsync(includeArchived: false, ct);
        var counted = all.Where(Counts).ToList();
        var ids = counted.Select(a => a.Id).ToHashSet();
        var cardIds = all.Where(a => a.Type.IsLiability()).Select(a => a.Id).ToHashSet();
        var caveats = new List<ForecastCaveat>();
        if (counted.Count == 0)
        {
            return new SafeToSpend(false, Money.Zero, today, today, null, [], [], []);
        }

        var open = await recurring.ListOpenAsync(today.AddDays(366), withMatches: false, ct);
        var profile = await spending.GetAsync(ct);
        var cardProfiles = (await cards.ListStatusesAsync(ct)).Where(s => s.Profile is not null).ToDictionary(s => s.Card.Id, s => s.Profile!);
        var learned = ProfileEvents(profile, cardProfiles, cardIds, today.AddDays(366)).Where(e => ids.Contains(e.AccountId)).ToList();
        var nextIncome = open
            .Where(o => o.Rule.Kind == RecurringKind.Income && ids.Contains(o.Rule.AccountId) && o.DueDate > today)
            .Select(o => new ExpectedIncome(o.Rule.Name, o.DueDate, o.Amount, false))
            .Concat(learned.Where(e => e.Kind == CashEventKind.Income && e.Date > today).Select(e => new ExpectedIncome(e.Name ?? string.Empty, e.Date, e.Amount, true)))
            .OrderBy(i => i.Date)
            .FirstOrDefault();
        var until = nextIncome is null ? today.AddDays(DefaultHorizonDays) : nextIncome.Date.AddDays(-1);
        if (nextIncome is null)
        {
            caveats.Add(ForecastCaveat.NoIncomePlanned);
        }

        var lines = new List<ForecastLine>();
        var balancesNow = await ledger.GetBalancesAsync(today, ct);
        var balancesUntil = await ledger.GetBalancesAsync(until, ct);
        var now = Money.Sum(counted.Select(a => balancesNow[a.Id]));
        lines.Add(new ForecastLine(ForecastLineKind.Balance, now));

        var later = Money.Sum(counted.Select(a => balancesUntil[a.Id])) - now;
        if (!later.IsZero)
        {
            lines.Add(new ForecastLine(ForecastLineKind.RecordedLater, later));
        }

        foreach (var o in open.Where(o => o.DueDate <= until))
        {
            var change = Effect(o, ids, cardIds);
            if (change.IsZero || (o.Rule.Kind == RecurringKind.Income && o.IsOverdue))
            {
                // Overdue income is not counted: it may still come, but it is not money yet.
                continue;
            }

            if (o.IsOverdue && !caveats.Contains(ForecastCaveat.OverdueItems))
            {
                caveats.Add(ForecastCaveat.OverdueItems);
            }

            lines.Add(new ForecastLine(change.IsNegative ? ForecastLineKind.Planned : ForecastLineKind.PlannedIn, change, o.Rule.Name, o.DueDate));
        }

        foreach (var e in learned.Where(e => e.Date <= until && e.Kind != CashEventKind.Usual))
        {
            lines.Add(new ForecastLine(e.Amount.IsNegative ? ForecastLineKind.Planned : ForecastLineKind.PlannedIn, e.Amount, e.Name, e.Date, Detected: true));
        }

        await AddCardsAsync(lines, caveats, ids, until, ct);

        var usual = Money.Sum(learned.Where(e => e.Kind == CashEventKind.Usual && e.Date <= until).Select(e => e.Amount));
        if (!usual.IsZero)
        {
            lines.Add(new ForecastLine(ForecastLineKind.UsualSpending, usual, Date: until));
        }

        if (!profile.IsKnown)
        {
            caveats.Add(ForecastCaveat.NoSpendingHistory);
        }

        var buffer = await settings.GetMinimumBufferAsync(ct);
        if (buffer.IsPositive)
        {
            lines.Add(new ForecastLine(ForecastLineKind.Buffer, buffer.Negate()));
        }

        return new SafeToSpend(true, Money.Sum(lines.Select(l => l.Amount)), today, until, nextIncome, counted, lines, caveats, profile);
    }

    /// <summary>Expected money in and out of the counted accounts in the coming <paramref name="days"/> days, overdue items included.</summary>
    public async Task<IReadOnlyList<CashFlow>> CashFlowAsync(IReadOnlyList<int> days, CancellationToken ct = default)
    {
        var today = clock.Today;
        var all = await accounts.ListAsync(includeArchived: false, ct);
        var ids = all.Where(Counts).Select(a => a.Id).ToHashSet();
        var cardIds = all.Where(a => a.Type.IsLiability()).Select(a => a.Id).ToHashSet();
        var max = days.Max();
        var open = await recurring.ListOpenAsync(today.AddDays(max), withMatches: false, ct);
        var settlements = await cards.ListSettlementsAsync(today.AddDays(max), ct);
        var profiles = (await cards.ListStatusesAsync(ct)).Where(s => s.Profile is not null).ToDictionary(s => s.Card.Id, s => s.Profile!);
        var learned = ProfileEvents(await spending.GetAsync(ct), profiles, cardIds, today.AddDays(max)).Where(e => ids.Contains(e.AccountId)).ToList();

        return days.Select(d =>
        {
            var until = today.AddDays(d);
            var changes = open.Where(o => o.DueDate <= until).Select(o => Effect(o, ids, cardIds))
                .Concat(settlements
                    .Where(s => s.FromAccountId is { } from && ids.Contains(from) && CardMath.Due(profiles[s.Card.Id], s.Period, today) <= until)
                    .Select(s => s.Amount.Negate()))
                .Concat(learned.Where(e => e.Date <= until).Select(e => e.Amount))
                .ToList();
            return new CashFlow(d, Money.Sum(changes.Where(c => c.IsPositive)), Money.Sum(changes.Where(c => c.IsNegative)).Negate());
        }).ToList();
    }

    private async Task AddCardsAsync(List<ForecastLine> lines, List<ForecastCaveat> caveats, HashSet<Guid> ids, DateOnly until, CancellationToken ct)
    {
        foreach (var (name, amount, due) in await CardOutflowsAsync(caveats, ids, until, ct))
        {
            lines.Add(new ForecastLine(ForecastLineKind.CardSettlement, amount, name, due));
        }
    }

    /// <summary>Card statements paid from a counted account up to <paramref name="until"/>, as money out on the day they are expected.</summary>
    private async Task<List<(string Name, Money Amount, DateOnly Due)>> CardOutflowsAsync(List<ForecastCaveat> caveats, HashSet<Guid> ids, DateOnly until, CancellationToken ct)
    {
        var today = clock.Today;
        var result = new List<(string, Money, DateOnly)>();
        var statuses = await cards.ListStatusesAsync(ct);
        if (statuses.Any(s => !s.IsSetUp))
        {
            caveats.Add(ForecastCaveat.CardNotSetUp);
        }

        var settlements = await cards.ListSettlementsAsync(until, ct);
        foreach (var s in settlements)
        {
            if (s.FromAccountId is not { } from)
            {
                if (!caveats.Contains(ForecastCaveat.SettlementAccountUnknown))
                {
                    caveats.Add(ForecastCaveat.SettlementAccountUnknown);
                }

                continue;
            }

            if (!ids.Contains(from))
            {
                continue;
            }

            if (s.Date is null && !caveats.Contains(ForecastCaveat.SettlementDateUnknown))
            {
                caveats.Add(ForecastCaveat.SettlementDateUnknown);
            }

            var profile = statuses.First(c => c.Card.Id == s.Card.Id).Profile!;
            result.Add((s.Card.Name, s.Amount.Negate(), CardMath.Due(profile, s.Period, today)));
        }

        return result;
    }

    /// <summary>
    /// Day-by-day balance of the counted accounts for the coming <paramref name="days"/> days: recorded later
    /// transactions, open planned items (overdue ones today, overdue income left out) and card statements on the
    /// day they are expected. Used to check purchases (spec §14); like every forecast it never changes data.
    /// </summary>
    public async Task<CashProjection> ProjectAsync(int days, CancellationToken ct = default) =>
        (await TimelineAsync(clock.Today.AddDays(days), ct)).Spendable();

    /// <summary>
    /// The expected balance of every money account (current, joint, savings, cash, other) up to <paramref name="until"/>
    /// (spec §16): today's balance, recorded later transactions, open planned items on each side of a transfer, and card
    /// statements on the account that pays them. Cards and valued accounts (pension, investments) are not projected.
    /// </summary>
    public async Task<ForecastTimeline> TimelineAsync(DateOnly until, CancellationToken ct = default)
    {
        var today = clock.Today;
        if (until < today)
        {
            until = today;
        }

        var all = await accounts.ListAsync(includeArchived: false, ct);
        var projected = all.Where(Projects).ToList();
        var ids = projected.Select(a => a.Id).ToHashSet();
        var counted = projected.Where(Counts).Select(a => a.Id).ToHashSet();
        var cardIds = all.Where(a => a.Type.IsLiability()).Select(a => a.Id).ToHashSet();
        var buffer = await settings.GetMinimumBufferAsync(ct);
        var caveats = new List<ForecastCaveat>();
        var events = new List<AccountEvent>();
        var balances = await ledger.GetBalancesAsync(today, ct);
        var start = projected.ToDictionary(a => a.Id, a => balances.TryGetValue(a.Id, out var b) ? b : Money.Zero);
        var statuses = await cards.ListStatusesAsync(ct);
        var profiles = statuses.Where(s => s.Profile is not null).ToDictionary(s => s.Card.Id, s => s.Profile!);
        var open = await recurring.ListOpenAsync(until, withMatches: false, ct);
        if (projected.Count == 0)
        {
            return new ForecastTimeline(today, until, buffer, projected, counted, start, events, caveats, open, profiles);
        }

        var later = await ledger.ListEntriesAsync(new TransactionQuery(From: today.AddDays(1), To: until), ct);
        foreach (var e in later.Where(e => ids.Contains(e.Transaction.AccountId)))
        {
            events.Add(new AccountEvent(e.Transaction.AccountId, e.Transaction.Date, e.Transaction.Amount, CashEventKind.RecordedLater, e.Transaction.Description));
        }

        foreach (var o in open.Where(o => o.DueDate <= until))
        {
            var income = o.Rule.Kind == RecurringKind.Income;
            if (income && o.IsOverdue)
            {
                // Overdue income is not counted: it may still come, but it is not money yet.
                continue;
            }

            var legs = Legs(o, ids, cardIds).ToList();
            if (legs.Count == 0)
            {
                continue;
            }

            if (o.IsOverdue && !Effect(o, counted, cardIds).IsZero && !caveats.Contains(ForecastCaveat.OverdueItems))
            {
                caveats.Add(ForecastCaveat.OverdueItems);
            }

            var date = o.DueDate < today ? today : o.DueDate;
            foreach (var (account, amount) in legs)
            {
                var kind = income ? CashEventKind.Income : amount.IsNegative ? CashEventKind.Planned : CashEventKind.PlannedIn;
                events.Add(new AccountEvent(account, date, amount, kind, o.Rule.Name, o.Rule.Id));
            }
        }

        if (statuses.Any(s => !s.IsSetUp))
        {
            caveats.Add(ForecastCaveat.CardNotSetUp);
        }

        foreach (var s in await cards.ListSettlementsAsync(until, ct))
        {
            if (s.FromAccountId is not { } from)
            {
                if (!caveats.Contains(ForecastCaveat.SettlementAccountUnknown))
                {
                    caveats.Add(ForecastCaveat.SettlementAccountUnknown);
                }

                continue;
            }

            if (!ids.Contains(from))
            {
                continue;
            }

            if (s.Date is null && counted.Contains(from) && !caveats.Contains(ForecastCaveat.SettlementDateUnknown))
            {
                caveats.Add(ForecastCaveat.SettlementDateUnknown);
            }

            events.Add(new AccountEvent(from, CardMath.Due(profiles[s.Card.Id], s.Period, today), s.Amount.Negate(), CashEventKind.CardSettlement, s.Card.Name, CardId: s.Card.Id));
        }

        var profile = await spending.GetAsync(ct);
        events.AddRange(ProfileEvents(profile, profiles, cardIds, until).Where(e => ids.Contains(e.AccountId)));
        if (!profile.IsKnown)
        {
            caveats.Add(ForecastCaveat.NoSpendingHistory);
        }

        if (!events.Any(e => e.Kind == CashEventKind.Income && e.Date > today && counted.Contains(e.AccountId)))
        {
            caveats.Add(ForecastCaveat.NoIncomePlanned);
        }

        return new ForecastTimeline(today, until, buffer, projected, counted, start, events, caveats, open, profiles);
    }

    /// <summary>
    /// What the history adds to a forecast (D-039), on the accounts the money moves on: recognised fixed costs (a card's on
    /// the account paying the card), transfers on both sides, recognised income, and the usual day-to-day spending a day
    /// at a time on the spending account. All marked as recognised, never as planned by the user.
    /// </summary>
    public static IEnumerable<AccountEvent> ProfileEvents(
        SpendingProfile profile, IReadOnlyDictionary<Guid, CreditCardProfile> cardProfiles, IReadOnlySet<Guid> cardIds, DateOnly until)
    {
        foreach (var f in profile.FixedCosts)
        {
            foreach (var date in f.DueDates(profile.Today, until))
            {
                switch (f.Kind)
                {
                    case RecurringKind.Income:
                        yield return new AccountEvent(f.AccountId, date, f.Amount, CashEventKind.Income, f.Name, Detected: true);
                        break;
                    case RecurringKind.Expense:
                        var payer = cardIds.Contains(f.AccountId)
                            ? cardProfiles.GetValueOrDefault(f.AccountId)?.SettlementAccountId ?? profile.SpendingAccountId
                            : f.AccountId;
                        if (payer is { } p)
                        {
                            yield return new AccountEvent(p, date, f.Amount.Negate(), CashEventKind.Planned, f.Name, Detected: true);
                        }

                        break;
                    default:
                        yield return new AccountEvent(f.AccountId, date, f.Amount.Negate(), CashEventKind.Planned, f.Name, Detected: true);
                        yield return new AccountEvent(f.ToAccountId!.Value, date, f.Amount, CashEventKind.PlannedIn, f.Name, Detected: true);
                        break;
                }
            }
        }

        if (profile.SpendingAccountId is { } account)
        {
            foreach (var (date, amount) in profile.UsualSpending(until))
            {
                yield return new AccountEvent(account, date, amount.Negate(), CashEventKind.Usual, null);
            }
        }
    }

    /// <summary>The accounts a timeline projects: those whose balance comes from the ledger and holds money.</summary>
    public static bool Projects(Account account) =>
        account.Status == AccountStatus.Active && !account.Type.IsLiability() && !account.Type.IsManuallyValued();

    /// <summary>
    /// Where an occurrence moves money among the projected accounts. Planned payments to a credit card are left out:
    /// the card's statement is calculated from the card itself, and counting both would count the same money twice.
    /// </summary>
    private static IEnumerable<(Guid Account, Money Amount)> Legs(PlannedOccurrence o, HashSet<Guid> ids, HashSet<Guid> cardIds)
    {
        var rule = o.Rule;
        switch (rule.Kind)
        {
            case RecurringKind.Income when ids.Contains(rule.AccountId):
                yield return (rule.AccountId, o.Amount);
                break;
            case RecurringKind.Expense when ids.Contains(rule.AccountId):
                yield return (rule.AccountId, o.Amount.Negate());
                break;
            case RecurringKind.Transfer when !cardIds.Contains(rule.ToAccountId!.Value):
                if (ids.Contains(rule.AccountId))
                {
                    yield return (rule.AccountId, o.Amount.Negate());
                }

                if (ids.Contains(rule.ToAccountId!.Value))
                {
                    yield return (rule.ToAccountId!.Value, o.Amount);
                }

                break;
        }
    }

    /// <summary>
    /// How an occurrence changes the counted accounts: income and expenses on them, and transfers
    /// crossing their edge. A transfer between two counted accounts changes nothing. Planned payments
    /// to a credit card are left out too: the card's statement is calculated from the card itself, and
    /// counting both would count the same money twice.
    /// </summary>
    private static Money Effect(PlannedOccurrence o, HashSet<Guid> ids, HashSet<Guid> cardIds)
    {
        var rule = o.Rule;
        var fromCounted = ids.Contains(rule.AccountId);
        if (rule.Kind == RecurringKind.Transfer && cardIds.Contains(rule.ToAccountId!.Value))
        {
            return Money.Zero;
        }

        return rule.Kind switch
        {
            RecurringKind.Income => fromCounted ? o.Amount : Money.Zero,
            RecurringKind.Expense => fromCounted ? o.Amount.Negate() : Money.Zero,
            _ => (fromCounted, ids.Contains(rule.ToAccountId!.Value)) switch
            {
                (true, false) => o.Amount.Negate(),
                (false, true) => o.Amount,
                _ => Money.Zero,
            },
        };
    }
}

/// <summary>What moves money on the counted accounts in a <see cref="CashProjection"/>.</summary>
public enum CashEventKind
{
    RecordedLater,
    Planned,
    PlannedIn,

    /// <summary>Expected income: never guaranteed, always labelled as expected.</summary>
    Income,
    CardSettlement,

    /// <summary>A purchase being considered; only exists in a what-if copy.</summary>
    Purchase,

    /// <summary>A change from a what-if scenario; never in the ledger.</summary>
    Scenario,

    /// <summary>A day of usual day-to-day spending, learned from the history (D-039).</summary>
    Usual,
}

/// <summary>One expected change of one projected account. <see cref="Amount"/> is signed: money out is negative.</summary>
public sealed record AccountEvent(
    Guid AccountId,
    DateOnly Date,
    Money Amount,
    CashEventKind Kind,
    string? Name,

    /// <summary>The recurring rule of a planned item.</summary>
    Guid? RuleId = null,

    /// <summary>The card of a statement payment.</summary>
    Guid? CardId = null,

    /// <summary>A fixed cost, transfer or income the app recognised in the history, not one the user planned (D-039).</summary>
    bool Detected = false);

/// <summary>
/// The expected balance of each money account from today to <see cref="Until"/> (spec §16). A forecast: it never
/// changes data. <see cref="With"/> gives a what-if copy and leaves this one unchanged.
/// </summary>
public sealed class ForecastTimeline(
    DateOnly today,
    DateOnly until,
    Money buffer,
    IReadOnlyList<Account> accounts,
    IReadOnlySet<Guid> counted,
    IReadOnlyDictionary<Guid, Money> start,
    IReadOnlyList<AccountEvent> events,
    IReadOnlyList<ForecastCaveat> caveats,
    IReadOnlyList<PlannedOccurrence> open,
    IReadOnlyDictionary<Guid, CreditCardProfile> cardProfiles)
{
    public DateOnly Today { get; } = today;
    public DateOnly Until { get; } = until;

    /// <summary>The minimum buffer for the spendable accounts.</summary>
    public Money Buffer { get; } = buffer;

    /// <summary>The projected accounts, in the user's order.</summary>
    public IReadOnlyList<Account> Accounts { get; } = accounts;

    /// <summary>The spendable accounts among them (personal current account and cash).</summary>
    public IReadOnlySet<Guid> Counted { get; } = counted;

    /// <summary>Each account's balance today.</summary>
    public IReadOnlyDictionary<Guid, Money> Start { get; } = start;

    public IReadOnlyList<AccountEvent> Events { get; } = events.OrderBy(e => e.Date).ToList();

    /// <summary>Caveats of the spendable forecast.</summary>
    public IReadOnlyList<ForecastCaveat> Caveats { get; } = caveats;

    /// <summary>The open planned items the forecast used, cards included.</summary>
    public IReadOnlyList<PlannedOccurrence> Open { get; } = open;

    public IReadOnlyDictionary<Guid, CreditCardProfile> CardProfiles { get; } = cardProfiles;

    public bool CanCalculate => Counted.Count > 0;

    /// <summary>The spendable accounts together, as safe-to-spend and affordability see them.</summary>
    public CashProjection Spendable() => new(
        Today,
        Until,
        Money.Sum(Accounts.Where(a => Counted.Contains(a.Id)).Select(a => Start[a.Id])),
        Buffer,
        Accounts.Where(a => Counted.Contains(a.Id)).ToList(),
        Events.Where(e => Counted.Contains(e.AccountId)).Select(ToCash).ToList(),
        Caveats);

    /// <summary>One account on its own. The minimum buffer only applies to the spendable accounts together.</summary>
    public CashProjection ForAccount(Guid accountId) => new(
        Today,
        Until,
        Start.GetValueOrDefault(accountId),
        Money.Zero,
        Accounts.Where(a => a.Id == accountId).ToList(),
        Events.Where(e => e.AccountId == accountId).Select(ToCash).ToList(),
        []);

    /// <summary>A copy with other events, for a what-if scenario.</summary>
    public ForecastTimeline With(IEnumerable<AccountEvent> replaced) =>
        new(Today, Until, Buffer, Accounts, Counted, Start, replaced.ToList(), Caveats, Open, CardProfiles);

    private static CashEvent ToCash(AccountEvent e) => new(e.Date, e.Amount, e.Kind, e.Name, e.Detected);
}

/// <summary>One expected change of the counted accounts. <see cref="Amount"/> is signed: money out is negative.</summary>
public sealed record CashEvent(DateOnly Date, Money Amount, CashEventKind Kind, string? Name, bool Detected = false);

/// <summary>
/// The expected balance of the counted accounts for every day from today to <see cref="Until"/>. A what-if purchase
/// is added with <see cref="With"/>, which returns a new projection and leaves this one unchanged.
/// </summary>
public sealed class CashProjection
{
    private readonly long[] _daily;

    public CashProjection(DateOnly today, DateOnly until, Money start, Money buffer, IReadOnlyList<Account> accounts, IReadOnlyList<CashEvent> events, IReadOnlyList<ForecastCaveat> caveats)
    {
        Today = today;
        Until = until;
        Start = start;
        Buffer = buffer;
        Accounts = accounts;
        Events = events.OrderBy(e => e.Date).ToList();
        Caveats = caveats;
        IncomeDates = Events.Where(e => e.Kind == CashEventKind.Income).Select(e => e.Date).Distinct().ToList();
        _daily = new long[until.DayNumber - today.DayNumber + 1];
        var running = start.Cents;
        var index = 0;
        for (var day = 0; day < _daily.Length; day++)
        {
            var date = today.AddDays(day);
            while (index < Events.Count && Events[index].Date <= date)
            {
                running += Events[index].Amount.Cents;
                index++;
            }

            _daily[day] = running;
        }
    }

    public DateOnly Today { get; }
    public DateOnly Until { get; }

    /// <summary>Balance of the counted accounts today.</summary>
    public Money Start { get; }

    /// <summary>The minimum buffer the user wants to keep.</summary>
    public Money Buffer { get; }

    public IReadOnlyList<Account> Accounts { get; }
    public IReadOnlyList<CashEvent> Events { get; }
    public IReadOnlyList<ForecastCaveat> Caveats { get; }

    /// <summary>Days with expected income, in order.</summary>
    public IReadOnlyList<DateOnly> IncomeDates { get; }

    public bool CanCalculate => Accounts.Count > 0;

    public bool HasIncome => IncomeDates.Any(d => d > Today);

    /// <summary>The expected balance at the end of <paramref name="date"/> (clamped to the projected days).</summary>
    public Money BalanceOn(DateOnly date) => Money.FromCents(_daily[Index(date)]);

    /// <summary>The lowest expected end-of-day balance between two dates, both included, and the first day it occurs.</summary>
    public (Money Balance, DateOnly Date) Lowest(DateOnly from, DateOnly to)
    {
        var start = Index(from);
        var end = Index(to);
        var best = start;
        for (var i = start + 1; i <= end; i++)
        {
            if (_daily[i] < _daily[best])
            {
                best = i;
            }
        }

        return (Money.FromCents(_daily[best]), Today.AddDays(best));
    }

    public DateOnly? NextIncomeAfter(DateOnly date) => IncomeDates.Cast<DateOnly?>().FirstOrDefault(d => d > date);

    /// <summary>A copy with one more change, for example a purchase being considered.</summary>
    public CashProjection With(CashEvent change) =>
        new(Today, Until, Start, Buffer, Accounts, [.. Events, change], Caveats);

    private int Index(DateOnly date) => Math.Clamp(date.DayNumber - Today.DayNumber, 0, _daily.Length - 1);
}
