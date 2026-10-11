using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Scenarios;

/// <summary>One change as the user enters it. Which fields count depends on <see cref="Kind"/>.</summary>
public sealed record ScenarioChangeRequest(
    ScenarioChangeKind Kind,
    Money Amount,
    DateOnly Date,
    string? Name = null,
    bool IsIncome = false,
    DateOnly? EndDate = null,
    Guid? AccountId = null,
    Guid? CardId = null,
    Guid? RuleId = null,
    Guid? WishlistItemId = null);

/// <summary>Why a change works out differently than it reads, or not at all. Shown with the change.</summary>
public enum ScenarioNote
{
    None,

    /// <summary>The date has passed; the forecast counts it from today.</summary>
    DatePassed,

    /// <summary>The account is archived, a card, or valued by hand, so it is not in the forecast.</summary>
    AccountNotForecast,

    /// <summary>The planned item no longer exists or has ended.</summary>
    PlannedItemGone,

    /// <summary>A planned payment to a card: the card's statement is forecast instead, so changing it changes nothing.</summary>
    PaymentToCard,

    CardNotSetUp,
    SettlementAccountUnknown,

    /// <summary>The purchase is more than the card's available credit now.</summary>
    OverLimit,

    /// <summary>Nothing of it falls within the forecast.</summary>
    OutsideForecast,
}

/// <summary>
/// A change as the forecast applied it: what it does in total over the shown months to the spendable accounts and to the
/// other accounts (savings, joint account), and when the money leaves for a card purchase.
/// </summary>
public sealed record AppliedChange(ScenarioChange Change, string Name, Money SpendableEffect, Money OtherEffect, ScenarioNote Note, DateOnly? PaidOn = null);

/// <summary>Expected balance at the end of a month, without and with the scenario, and the lowest balance in it.</summary>
public sealed record MonthComparison(DateOnly Month, Money Baseline, Money Scenario, Money BaselineLowest, Money ScenarioLowest)
{
    public Money Difference => Scenario - Baseline;
}

/// <summary>Expected balance of the spendable accounts a number of days ahead (spec §16: 7, 30 and 90 days).</summary>
public sealed record HorizonComparison(int Days, DateOnly Date, Money Baseline, Money Scenario)
{
    public Money Difference => Scenario - Baseline;
}

public enum ShortfallKind
{
    /// <summary>The spendable accounts together go below the minimum buffer.</summary>
    BelowBuffer,

    /// <summary>The balance goes below zero.</summary>
    BelowZero,
}

/// <summary>The first day a balance falls short. <see cref="Account"/> is null for the spendable accounts together.</summary>
public sealed record Shortfall(Account? Account, ShortfallKind Kind, DateOnly Date, Money Balance);

/// <summary>
/// A scenario next to the baseline (spec §16): month by month for a year, the 7/30/90-day horizons, shortfall warnings
/// per account, and what it does to the wishlist. Everything is calculated; nothing is stored or changed.
/// </summary>
public sealed record ScenarioResult(
    DateOnly Today,

    /// <summary>The last day of the twelfth month shown.</summary>
    DateOnly LastDay,
    ForecastTimeline Baseline,
    ForecastTimeline Projected,
    IReadOnlyList<AppliedChange> Changes,
    IReadOnlyList<HorizonComparison> Horizons,
    IReadOnlyList<Shortfall> BaselineShortfalls,
    IReadOnlyList<Shortfall> ScenarioShortfalls,
    IReadOnlyList<WishlistEffect> Wishlist)
{
    public bool CanCalculate => Baseline.CanCalculate;

    public bool HasChanges => Changes.Count > 0;

    /// <summary>Month by month for the spendable accounts together, or for one account.</summary>
    public IReadOnlyList<MonthComparison> Months(Guid? accountId = null) => accountId is { } id
        ? ScenarioMath.Months(Baseline.ForAccount(id), Projected.ForAccount(id), ScenarioMath.MonthsShown)
        : ScenarioMath.Months(Baseline.Spendable(), Projected.Spendable(), ScenarioMath.MonthsShown);
}

/// <summary>
/// What-if scenarios (spec §16): renovation costs, purchase timing and payment method, salary, recurring costs, joint
/// transfers and wishlist purchases laid over the forecast. Scenarios are saved so they can be compared again later,
/// but they never change the ledger, planned items or the wishlist.
/// </summary>
public sealed class ScenarioService(
    IScenarioRepository scenarios,
    IAccountRepository accounts,
    IWishlistRepository wishlist,
    RecurringService recurring,
    ForecastService forecast,
    CreditCardService cards,
    IAuditLog audit,
    IClock clock)
{
    public event EventHandler? Changed;

    public Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken ct = default) => scenarios.ListAsync(ct);

    public Task<IReadOnlyList<ScenarioChange>> ListChangesAsync(Guid scenarioId, CancellationToken ct = default) =>
        scenarios.ListChangesAsync(scenarioId, ct);

    /// <summary>A new scenario, optionally with its first changes (for example from the assistant), saved together.</summary>
    public async Task<Scenario> CreateAsync(string name, string? note = null, IReadOnlyList<ScenarioChangeRequest>? changes = null, CancellationToken ct = default)
    {
        var scenario = Scenario.Create(name, note, clock.Now);
        var list = new List<ScenarioChange>();
        foreach (var request in changes ?? [])
        {
            list.Add(await BuildAsync(scenario.Id, request, ct));
        }

        await scenarios.AddAsync(scenario, list, ct);
        await audit.RecordAsync("Scenario", scenario.Id, "Created", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return scenario;
    }

    public async Task RenameAsync(Guid id, string name, string? note, CancellationToken ct = default)
    {
        var scenario = await GetAsync(id, ct);
        scenario.Change(name, note, clock.Now);
        await scenarios.UpdateAsync(scenario, ct);
        await audit.RecordAsync("Scenario", id, "Edited", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        _ = await GetAsync(id, ct);
        await scenarios.RemoveAsync(id, ct);
        await audit.RecordAsync("Scenario", id, "Removed", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<ScenarioChange> AddChangeAsync(Guid scenarioId, ScenarioChangeRequest request, CancellationToken ct = default)
    {
        var scenario = await GetAsync(scenarioId, ct);
        var change = await BuildAsync(scenarioId, request, ct);
        await scenarios.AddChangeAsync(change, ct);
        scenario.Change(scenario.Name, scenario.Note, clock.Now);
        await scenarios.UpdateAsync(scenario, ct);
        await audit.RecordAsync("Scenario", scenarioId, "ChangeAdded", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return change;
    }

    public async Task RemoveChangeAsync(Guid scenarioId, Guid changeId, CancellationToken ct = default)
    {
        await scenarios.RemoveChangeAsync(changeId, ct);
        await audit.RecordAsync("Scenario", scenarioId, "ChangeRemoved", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A saved scenario next to the baseline; without a scenario, the baseline alone.</summary>
    public async Task<ScenarioResult> EvaluateAsync(Guid? scenarioId, CancellationToken ct = default)
    {
        var changes = scenarioId is { } id ? await scenarios.ListChangesAsync(id, ct) : [];
        return await EvaluateAsync(changes, ct);
    }

    /// <summary>Changes that are not saved (for example a question to the assistant) next to the baseline.</summary>
    public async Task<ScenarioResult> EvaluateAsync(IReadOnlyList<ScenarioChangeRequest> requests, CancellationToken ct = default)
    {
        var changes = new List<ScenarioChange>();
        foreach (var request in requests)
        {
            changes.Add(await BuildAsync(Guid.Empty, request, ct));
        }

        return await EvaluateAsync(changes, ct);
    }

    private async Task<ScenarioResult> EvaluateAsync(IReadOnlyList<ScenarioChange> changes, CancellationToken ct)
    {
        var today = clock.Today;
        var baseline = await forecast.TimelineAsync(today.AddDays(ScenarioMath.ForecastDays), ct);
        var rules = (await recurring.ListRulesAsync(includeEnded: true, ct)).ToDictionary(r => r.Id);
        var all = (await accounts.ListAsync(includeArchived: true, ct)).ToDictionary(a => a.Id);
        var credit = (await cards.ListStatusesAsync(ct)).ToDictionary(s => s.Card.Id, s => s.AvailableCredit);
        var (projected, applied) = ScenarioMath.Apply(baseline, changes, rules, all, credit);

        var items = (await wishlist.ListAsync(ct)).Where(i => i.IsOpen).ToList();
        var bought = changes.Where(c => c.WishlistItemId is not null).Select(c => c.WishlistItemId!.Value).ToHashSet();
        var before = baseline.Spendable();
        var after = projected.Spendable();
        var effects = before.CanCalculate
            ? items.Where(i => !bought.Contains(i.Id))
                .OrderBy(i => i.Priority).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new WishlistEffect(i, AffordabilityService.EarliestPrudent(before, i.ExpectedPrice), AffordabilityService.EarliestPrudent(after, i.ExpectedPrice)))
                .ToList()
            : [];

        var lastDay = ScenarioMath.LastDay(today);
        return new ScenarioResult(
            today,
            lastDay,
            baseline,
            projected,
            applied,
            ScenarioMath.Horizons.Select(d => new HorizonComparison(d, today.AddDays(d), before.BalanceOn(today.AddDays(d)), after.BalanceOn(today.AddDays(d)))).ToList(),
            ScenarioMath.Shortfalls(baseline, lastDay),
            ScenarioMath.Shortfalls(projected, lastDay),
            effects);
    }

    private async Task<ScenarioChange> BuildAsync(Guid scenarioId, ScenarioChangeRequest r, CancellationToken ct)
    {
        var now = clock.Now;
        switch (r.Kind)
        {
            case ScenarioChangeKind.OneOff:
                return ScenarioChange.OneOff(scenarioId, r.Name ?? string.Empty, r.IsIncome, r.Amount, r.Date, await MoneyAccountAsync(r.AccountId, ct), now);
            case ScenarioChangeKind.Monthly:
                return ScenarioChange.Monthly(scenarioId, r.Name ?? string.Empty, r.IsIncome, r.Amount, r.Date, r.EndDate, await MoneyAccountAsync(r.AccountId, ct), now);
            case ScenarioChangeKind.ChangePlanned:
                var rules = await recurring.ListRulesAsync(includeEnded: true, ct);
                if (r.RuleId is not { } ruleId || rules.All(x => x.Id != ruleId))
                {
                    throw new DomainValidationException("scenario.planned.required");
                }

                return ScenarioChange.ChangePlanned(scenarioId, ruleId, r.Date, r.Amount, now);
            default:
                if (r.CardId is null && r.AccountId is null)
                {
                    throw new DomainValidationException("scenario.payment.required");
                }

                Guid? account = r.CardId is null ? await MoneyAccountAsync(r.AccountId, ct) : null;
                if (r.CardId is { } cardId && (await accounts.FindAsync(cardId, ct))?.Type.IsLiability() != true)
                {
                    throw new DomainValidationException("scenario.payment.required");
                }

                var name = r.Name;
                if (r.WishlistItemId is { } itemId)
                {
                    var item = await wishlist.FindAsync(itemId, ct) ?? throw new DomainValidationException("wishlist.notFound");
                    name = string.IsNullOrWhiteSpace(name) ? item.Name : name;
                }

                return ScenarioChange.Purchase(scenarioId, name ?? string.Empty, r.Amount, r.Date, account, r.CardId, r.WishlistItemId, now);
        }
    }

    /// <summary>A one-off or monthly amount goes to or from an account the forecast follows.</summary>
    private async Task<Guid> MoneyAccountAsync(Guid? id, CancellationToken ct)
    {
        var account = id is { } x ? await accounts.FindAsync(x, ct) : null;
        if (account is null || !ForecastService.Projects(account))
        {
            throw new DomainValidationException("scenario.account.required");
        }

        return account.Id;
    }

    private async Task<Scenario> GetAsync(Guid id, CancellationToken ct) =>
        await scenarios.FindAsync(id, ct) ?? throw new DomainValidationException("scenario.notFound");
}

/// <summary>The scenario calculations, free of storage so they are easy to test.</summary>
public static class ScenarioMath
{
    /// <summary>Months shown, the current one included (spec §16: month by month up to 12 months).</summary>
    public const int MonthsShown = 12;

    /// <summary>Days forecast: long enough for the wishlist's earliest dates (a year plus the look-ahead), like the wishlist itself.</summary>
    public const int ForecastDays = AffordabilityService.ScanDays + AffordabilityService.LookAheadDays;

    public static readonly int[] Horizons = [7, 30, 90];

    public static DateOnly LastDay(DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(MonthsShown);
        return first.AddDays(-1);
    }

    /// <summary>The timeline with the changes applied, and how each change worked out.</summary>
    public static (ForecastTimeline Timeline, IReadOnlyList<AppliedChange> Applied) Apply(
        ForecastTimeline baseline,
        IReadOnlyList<ScenarioChange> changes,
        IReadOnlyDictionary<Guid, RecurringRule> rules,
        IReadOnlyDictionary<Guid, Account> accounts,
        IReadOnlyDictionary<Guid, Money> availableCredit)
    {
        var today = baseline.Today;
        var lastDay = LastDay(today);
        var projected = baseline.Accounts.Select(a => a.Id).ToHashSet();
        var events = baseline.Events.ToList();
        var applied = new List<AppliedChange>();

        foreach (var change in changes)
        {
            var note = ScenarioNote.None;
            DateOnly? paidOn = null;
            var name = change.Name;
            var before = Totals(events, baseline.Counted, lastDay);
            var date = change.Date < today ? today : change.Date;
            if ((change.Kind is ScenarioChangeKind.OneOff or ScenarioChangeKind.Purchase) && change.Date < today)
            {
                note = ScenarioNote.DatePassed;
            }

            switch (change.Kind)
            {
                case ScenarioChangeKind.OneOff:
                case ScenarioChangeKind.Purchase when change.AccountId is not null:
                    if (!projected.Contains(change.AccountId!.Value))
                    {
                        note = ScenarioNote.AccountNotForecast;
                        break;
                    }

                    events.Add(new AccountEvent(change.AccountId.Value, date, change.IsIncome ? change.Amount : change.Amount.Negate(), CashEventKind.Scenario, name));
                    paidOn = date;
                    break;

                case ScenarioChangeKind.Monthly:
                    if (!projected.Contains(change.AccountId!.Value))
                    {
                        note = ScenarioNote.AccountNotForecast;
                        break;
                    }

                    for (var n = 0; ; n++)
                    {
                        var day = change.Date.AddMonths(n);
                        if (day > baseline.Until || (change.EndDate is { } end && day > end))
                        {
                            break;
                        }

                        if (day >= today)
                        {
                            events.Add(new AccountEvent(change.AccountId.Value, day, change.IsIncome ? change.Amount : change.Amount.Negate(), CashEventKind.Scenario, name));
                        }
                    }

                    break;

                case ScenarioChangeKind.ChangePlanned:
                    if (!rules.TryGetValue(change.RuleId!.Value, out var rule))
                    {
                        note = ScenarioNote.PlannedItemGone;
                        break;
                    }

                    name = rule.Name;
                    note = ChangePlanned(baseline, events, rule, change, accounts);
                    break;

                default:
                    var card = change.CardId!.Value;
                    note = CardPurchase(baseline, events, change, card, date, availableCredit, out paidOn) ?? note;
                    break;
            }

            var after = Totals(events, baseline.Counted, lastDay);
            var spendable = after.Spendable - before.Spendable;
            var other = after.Other - before.Other;
            if (note == ScenarioNote.None && spendable.IsZero && other.IsZero && change.Kind != ScenarioChangeKind.ChangePlanned)
            {
                note = ScenarioNote.OutsideForecast;
            }

            applied.Add(new AppliedChange(change, name, spendable, other, note, paidOn));
        }

        return (baseline.With(events), applied);
    }

    /// <summary>Month-end and lowest balances for the shown months; the current month starts today.</summary>
    public static IReadOnlyList<MonthComparison> Months(CashProjection baseline, CashProjection scenario, int months)
    {
        var today = baseline.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        var result = new List<MonthComparison>(months);
        for (var i = 0; i < months; i++)
        {
            var month = first.AddMonths(i);
            var start = i == 0 ? today : month;
            var end = month.AddMonths(1).AddDays(-1);
            result.Add(new MonthComparison(
                month,
                baseline.BalanceOn(end),
                scenario.BalanceOn(end),
                baseline.Lowest(start, end).Balance,
                scenario.Lowest(start, end).Balance));
        }

        return result;
    }

    /// <summary>
    /// The first day the spendable accounts together go below the buffer and below zero, and the first day any account
    /// goes below zero, up to <paramref name="lastDay"/>.
    /// </summary>
    public static IReadOnlyList<Shortfall> Shortfalls(ForecastTimeline timeline, DateOnly lastDay)
    {
        var result = new List<Shortfall>();
        if (timeline.CanCalculate)
        {
            var spendable = timeline.Spendable();
            if (spendable.Buffer.IsPositive && First(spendable, lastDay, b => b < spendable.Buffer) is { } low)
            {
                result.Add(new Shortfall(null, ShortfallKind.BelowBuffer, low.Date, low.Balance));
            }

            if (First(spendable, lastDay, b => b.IsNegative) is { } neg)
            {
                result.Add(new Shortfall(null, ShortfallKind.BelowZero, neg.Date, neg.Balance));
            }
        }

        foreach (var account in timeline.Accounts)
        {
            if (First(timeline.ForAccount(account.Id), lastDay, b => b.IsNegative) is { } neg)
            {
                result.Add(new Shortfall(account, ShortfallKind.BelowZero, neg.Date, neg.Balance));
            }
        }

        return result;
    }

    private static (DateOnly Date, Money Balance)? First(CashProjection projection, DateOnly lastDay, Func<Money, bool> test)
    {
        for (var date = projection.Today; date <= lastDay && date <= projection.Until; date = date.AddDays(1))
        {
            var balance = projection.BalanceOn(date);
            if (test(balance))
            {
                return (date, balance);
            }
        }

        return null;
    }

    private static ScenarioNote ChangePlanned(ForecastTimeline baseline, List<AccountEvent> events, RecurringRule rule, ScenarioChange change, IReadOnlyDictionary<Guid, Account> accounts)
    {
        var from = change.Date;
        if (rule.Kind == RecurringKind.Transfer && accounts.TryGetValue(rule.ToAccountId!.Value, out var to) && to.Type.IsLiability())
        {
            return ScenarioNote.PaymentToCard;
        }

        if (rule.Kind == RecurringKind.Expense && accounts.TryGetValue(rule.AccountId, out var card) && card.Type.IsLiability())
        {
            // A card payment: it changes the statement of its period, paid later from the paying account.
            if (!baseline.CardProfiles.TryGetValue(card.Id, out var profile))
            {
                return ScenarioNote.CardNotSetUp;
            }

            if (profile.SettlementAccountId is not { } payer)
            {
                return ScenarioNote.SettlementAccountUnknown;
            }

            if (!baseline.Accounts.Any(a => a.Id == payer))
            {
                return ScenarioNote.AccountNotForecast;
            }

            foreach (var o in baseline.Open.Where(o => o.Rule.Id == rule.Id && o.DueDate >= from))
            {
                var day = o.DueDate < baseline.Today ? baseline.Today : o.DueDate;
                var due = CardMath.Due(profile, profile.PeriodOf(day), baseline.Today);
                var delta = change.Amount - o.Amount;
                if (!delta.IsZero && due <= baseline.Until)
                {
                    events.Add(new AccountEvent(payer, due, delta.Negate(), CashEventKind.Scenario, rule.Name, CardId: card.Id));
                }
            }

            return ScenarioNote.None;
        }

        if (!rule.IsActive && !events.Any(e => e.RuleId == rule.Id))
        {
            return ScenarioNote.PlannedItemGone;
        }

        for (var i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.RuleId != rule.Id || e.Date < from)
            {
                continue;
            }

            if (change.Amount.IsZero)
            {
                events.RemoveAt(i);
            }
            else
            {
                events[i] = e with { Amount = e.Amount.IsNegative ? change.Amount.Negate() : change.Amount };
            }
        }

        return ScenarioNote.None;
    }

    private static ScenarioNote? CardPurchase(ForecastTimeline baseline, List<AccountEvent> events, ScenarioChange change, Guid card, DateOnly date, IReadOnlyDictionary<Guid, Money> availableCredit, out DateOnly? paidOn)
    {
        paidOn = null;
        if (!baseline.CardProfiles.TryGetValue(card, out var profile))
        {
            return ScenarioNote.CardNotSetUp;
        }

        if (profile.SettlementAccountId is not { } payer)
        {
            return ScenarioNote.SettlementAccountUnknown;
        }

        if (!baseline.Accounts.Any(a => a.Id == payer))
        {
            return ScenarioNote.AccountNotForecast;
        }

        // The purchase joins the statement of its period and leaves the paying account on that statement's debit day.
        var due = CardMath.Due(profile, profile.PeriodOf(date), baseline.Today);
        events.Add(new AccountEvent(payer, due, change.Amount.Negate(), CashEventKind.Scenario, change.Name, CardId: card));
        paidOn = due;
        return availableCredit.TryGetValue(card, out var credit) && credit < change.Amount ? ScenarioNote.OverLimit : null;
    }

    private static (Money Spendable, Money Other) Totals(List<AccountEvent> events, IReadOnlySet<Guid> counted, DateOnly lastDay)
    {
        var shown = events.Where(e => e.Date <= lastDay).ToList();
        return (Money.Sum(shown.Where(e => counted.Contains(e.AccountId)).Select(e => e.Amount)),
            Money.Sum(shown.Where(e => !counted.Contains(e.AccountId)).Select(e => e.Amount)));
    }
}
