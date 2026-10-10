using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
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
}

/// <summary>One line of the safe-to-spend breakdown. <see cref="Amount"/> is signed: money out is negative.</summary>
public sealed record ForecastLine(ForecastLineKind Kind, Money Amount, string? Name = null, DateOnly? Date = null);

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

    /// <summary>The next expected income, when one is planned. Expected, never guaranteed.</summary>
    PlannedOccurrence? NextIncome,
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<ForecastLine> Lines,
    IReadOnlyList<ForecastCaveat> Caveats);

/// <summary>Expected money in and out over a number of days (spec §13: 7, 30 and 90 days).</summary>
public sealed record CashFlow(int Days, Money ExpectedIn, Money ExpectedOut)
{
    public Money Net => ExpectedIn - ExpectedOut;
}

/// <summary>
/// Forecasts from recorded transactions, open planned items and card statements. Planned items
/// affect forecasts only; balances stay the ledger's (spec §12). Expected income is always labelled
/// as expected and never counted before it arrives in safe-to-spend.
/// </summary>
public sealed class ForecastService(
    IAccountRepository accounts,
    LedgerService ledger,
    RecurringService recurring,
    CreditCardService cards,
    AppSettingsService settings,
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
        var nextIncome = open.FirstOrDefault(o => o.Rule.Kind == RecurringKind.Income && ids.Contains(o.Rule.AccountId) && o.DueDate > today);
        var until = nextIncome is null ? today.AddDays(DefaultHorizonDays) : nextIncome.DueDate.AddDays(-1);
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

        await AddCardsAsync(lines, caveats, ids, until, ct);

        var buffer = await settings.GetMinimumBufferAsync(ct);
        if (buffer.IsPositive)
        {
            lines.Add(new ForecastLine(ForecastLineKind.Buffer, buffer.Negate()));
        }

        return new SafeToSpend(true, Money.Sum(lines.Select(l => l.Amount)), today, until, nextIncome, counted, lines, caveats);
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

        return days.Select(d =>
        {
            var until = today.AddDays(d);
            var changes = open.Where(o => o.DueDate <= until).Select(o => Effect(o, ids, cardIds))
                .Concat(settlements
                    .Where(s => s.FromAccountId is { } from && ids.Contains(from) && CardMath.Due(profiles[s.Card.Id], s.Period, today) <= until)
                    .Select(s => s.Amount.Negate()))
                .ToList();
            return new CashFlow(d, Money.Sum(changes.Where(c => c.IsPositive)), Money.Sum(changes.Where(c => c.IsNegative)).Negate());
        }).ToList();
    }

    private async Task AddCardsAsync(List<ForecastLine> lines, List<ForecastCaveat> caveats, HashSet<Guid> ids, DateOnly until, CancellationToken ct)
    {
        var today = clock.Today;
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
            lines.Add(new ForecastLine(ForecastLineKind.CardSettlement, s.Amount.Negate(), s.Card.Name, CardMath.Due(profile, s.Period, today)));
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
