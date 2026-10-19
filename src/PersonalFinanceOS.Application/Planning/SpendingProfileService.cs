using System.Globalization;
using System.Text;
using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Planning;

/// <summary>
/// A monthly payment, transfer or income the app recognised in the history by itself: the same counterparty, about
/// the same amount, once a month, in almost every learned month (D-039). It is expected again a month after it was
/// last seen. It never becomes a planned item and never changes data.
/// </summary>
public sealed record DetectedFixedCost(
    /// <summary>The description of the last occurrence, or its category when it has none.</summary>
    string Name,
    RecurringKind Kind,
    Guid AccountId,

    /// <summary>The receiving account of a transfer.</summary>
    Guid? ToAccountId,
    Guid? CategoryId,

    /// <summary>Positive. The last amount for costs; the lower of the last and the usual amount for income.</summary>
    Money Amount,
    DateOnly LastSeen,

    /// <summary>In how many of the learned months it occurred.</summary>
    int MonthsSeen)
{
    /// <summary>The day of the month it is expected on: the day it was last seen.</summary>
    public int Day => LastSeen.Day;

    /// <summary>The expected dates from <paramref name="today"/> up to <paramref name="until"/>, one a month.</summary>
    public IEnumerable<DateOnly> DueDates(DateOnly today, DateOnly until)
    {
        for (var i = 1; ; i++)
        {
            var month = new DateOnly(LastSeen.Year, LastSeen.Month, 1).AddMonths(i);
            var date = new DateOnly(month.Year, month.Month, Math.Min(Day, DateTime.DaysInMonth(month.Year, month.Month)));
            if (date < today)
            {
                // Not seen yet this time. A cost is still expected, so it counts today; income only counts once it arrives.
                if (Kind != RecurringKind.Income && date.AddMonths(1) >= today)
                {
                    yield return today;
                }

                continue;
            }

            if (date > until)
            {
                yield break;
            }

            yield return date;
        }
    }
}

/// <summary>
/// What the user's money usually does, learned from the last complete months of history (D-039): the fixed monthly
/// costs and income the app recognises by itself, and the usual day-to-day spending on top of them. Budgets play no
/// part. Only money that leaves the spendable accounts counts: the personal current account, cash, and the credit
/// cards they pay.
/// </summary>
public sealed record SpendingProfile(
    DateOnly Today,

    /// <summary>The complete months learned from (0 when there is not enough history).</summary>
    int Months,

    /// <summary>The first and last day learned from.</summary>
    DateOnly From,
    DateOnly To,

    /// <summary>Recognised fixed costs and income that no planned item covers yet.</summary>
    IReadOnlyList<DetectedFixedCost> FixedCosts,

    /// <summary>Usual day-to-day spending a month: the median month, without fixed costs and planned items.</summary>
    Money UsualMonthly,

    /// <summary>Day-to-day spending recorded this month so far.</summary>
    Money SpentThisMonth,

    /// <summary>What is still expected this month: the usual month minus what is spent, but never less than the usual pace for the days left.</summary>
    Money RestOfMonth,

    /// <summary>The account usual spending is taken from: the first personal current account (or cash).</summary>
    Guid? SpendingAccountId)
{
    public bool IsKnown => Months >= SpendingProfileService.MinimumMonths;

    /// <summary>Usual day-to-day spending from tomorrow up to <paramref name="until"/>, one amount a day.</summary>
    public IEnumerable<(DateOnly Date, Money Amount)> UsualSpending(DateOnly until)
    {
        if (!IsKnown || SpendingAccountId is null)
        {
            yield break;
        }

        var start = Today.AddDays(1);
        while (start <= until)
        {
            var monthEnd = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            var isThisMonth = start.Year == Today.Year && start.Month == Today.Month;
            var total = isThisMonth ? RestOfMonth : UsualMonthly;
            var days = monthEnd.DayNumber - start.DayNumber + 1;
            var last = monthEnd < until ? monthEnd : until;
            for (var d = start; d <= last; d = d.AddDays(1))
            {
                var i = d.DayNumber - start.DayNumber;
                var amount = Share(total, i + 1, days) - Share(total, i, days);
                if (amount.IsPositive)
                {
                    yield return (d, amount);
                }
            }

            start = monthEnd.AddDays(1);
        }
    }

    /// <summary>The usual spending between tomorrow and <paramref name="until"/> together.</summary>
    public Money UsualSpendingUntil(DateOnly until) => Money.Sum(UsualSpending(until).Select(u => u.Amount));

    private static Money Share(Money total, int part, int of) =>
        Money.FromCents((long)RoundingPolicy.RoundToCents(total.Cents * (decimal)part / of));
}

/// <summary>
/// Learns the <see cref="SpendingProfile"/> from the ledger (D-039). Read-only and deterministic: the same history
/// always gives the same profile.
/// </summary>
public sealed class SpendingProfileService(
    IAccountRepository accounts,
    ICategoryRepository categories,
    ITransactionRepository transactions,
    ICreditCardRepository cards,
    RecurringService recurring,
    IClock clock)
{
    /// <summary>At most this many complete months are learned from.</summary>
    public const int HistoryMonths = 6;

    /// <summary>Fewer complete months than this is too little to say what is usual.</summary>
    public const int MinimumMonths = 2;

    /// <summary>A fixed cost or transfer stays within this share of its usual amount…</summary>
    public const int FixedTolerancePercent = 10;

    /// <summary>…and income within this one (overtime, a different number of working days).</summary>
    public const int IncomeTolerancePercent = 20;

    /// <summary>A planned item covers a recognised payment when its amount is this close.</summary>
    public const int PlannedTolerancePercent = 25;

    private static readonly HashSet<string> MonthWords =
    [
        "januari", "februari", "maart", "april", "mei", "juni", "juli", "augustus", "september", "oktober", "november", "december",
        "january", "february", "march", "may", "june", "july", "august", "october",
        "jan", "feb", "mrt", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "okt", "oct", "nov", "dec",
    ];

    public async Task<SpendingProfile> GetAsync(CancellationToken ct = default)
    {
        var today = clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var all = await accounts.ListAsync(includeArchived: false, ct);
        var counted = all.Where(ForecastService.Counts).ToList();
        var spendingAccount = counted.OrderBy(a => a.Type == AccountType.PersonalCurrent ? 0 : 1).ThenBy(a => a.SortOrder).FirstOrDefault();
        var unknown = new SpendingProfile(today, 0, thisMonth, thisMonth.AddDays(-1), [], Money.Zero, Money.Zero, Money.Zero, spendingAccount?.Id);
        if (counted.Count == 0)
        {
            return unknown;
        }

        // Complete months only: on or after the first balance date of the spendable accounts, and already over.
        var historyStart = counted.Min(a => a.OpeningDate);
        var months = Enumerable.Range(1, HistoryMonths)
            .Select(i => thisMonth.AddMonths(-i))
            .Where(m => historyStart <= m)
            .OrderBy(m => m)
            .ToList();
        if (months.Count < MinimumMonths)
        {
            return unknown with { Months = months.Count };
        }

        var countedIds = counted.Select(a => a.Id).ToHashSet();
        var profiles = (await cards.ListAsync(ct)).ToDictionary(p => p.AccountId);
        var paidCards = all
            .Where(a => a.Type.IsLiability() && (profiles.GetValueOrDefault(a.Id)?.SettlementAccountId is not { } payer || countedIds.Contains(payer)))
            .Select(a => a.Id)
            .ToHashSet();
        var cardIds = all.Where(a => a.Type.IsLiability()).Select(a => a.Id).ToHashSet();
        var from = months[0];
        var lastComplete = thisMonth.AddDays(-1);
        var lines = (await transactions.ListAsync(new TransactionQuery(From: from), ct)).Where(t => t.IsActive).ToList();

        // Transfers are matched with their other side, so a transfer from the current account to savings is one flow.
        var otherSide = lines.Where(t => t.TransferId is not null)
            .GroupBy(t => t.TransferId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var flows = new List<Flow>();
        foreach (var t in lines)
        {
            if (t.Type.CategoryKind() == CategoryKind.Expense && (countedIds.Contains(t.AccountId) || paidCards.Contains(t.AccountId)))
            {
                flows.Add(new Flow(t, RecurringKind.Expense, t.AccountId, null, t.Amount.Negate()));
            }
            else if (t.Type == TransactionType.Income && countedIds.Contains(t.AccountId))
            {
                flows.Add(new Flow(t, RecurringKind.Income, t.AccountId, null, t.Amount));
            }
            else if (t.IsTransfer && t.Amount.IsNegative && countedIds.Contains(t.AccountId)
                     && otherSide.GetValueOrDefault(t.TransferId!.Value)?.FirstOrDefault(o => o.Id != t.Id) is { } to
                     && !countedIds.Contains(to.AccountId) && !cardIds.Contains(to.AccountId))
            {
                // Money put aside or sent to the joint account. Card payments are left out: the card's statement counts.
                flows.Add(new Flow(t, RecurringKind.Transfer, t.AccountId, to.AccountId, t.Amount.Negate()));
            }
        }

        var cats = (await categories.ListAsync(includeArchived: true, ct)).ToDictionary(c => c.Id);
        var rules = (await recurring.ListRulesAsync(includeEnded: false, ct)).Where(r => r.IsActive).ToList();
        var settling = await recurring.ListSettlingTransactionIdsAsync(ct);

        var fixedCosts = new List<DetectedFixedCost>();
        var recurringIds = new HashSet<Guid>();
        foreach (var group in flows.GroupBy(f => (f.Kind, f.AccountId, f.ToAccountId, Key: Key(f.Transaction))).Where(g => g.Key.Key.Length > 0))
        {
            var list = group.OrderBy(f => f.Transaction.Date).ToList();
            var learned = list.Where(f => f.Transaction.Date >= from && f.Transaction.Date <= lastComplete).ToList();
            var monthsSeen = learned.Select(f => new DateOnly(f.Transaction.Date.Year, f.Transaction.Date.Month, 1)).Distinct().Count();
            var latest = list[^1];
            var needed = Math.Max(MinimumMonths, (int)Math.Ceiling(months.Count * 0.75));
            if (monthsSeen < needed || learned.Count > monthsSeen + 1 || latest.Transaction.Date < months[^1] || latest.Amount <= Money.Zero)
            {
                continue;
            }

            var amounts = learned.Select(f => f.Amount).ToList();
            var usual = Median(amounts);
            var tolerance = group.Key.Kind == RecurringKind.Income ? IncomeTolerancePercent : FixedTolerancePercent;
            if (!usual.IsPositive || list.Any(f => (f.Amount - usual).Abs().Cents * 100 > tolerance * usual.Cents))
            {
                continue;
            }

            foreach (var f in list)
            {
                recurringIds.Add(f.Transaction.Id);
            }

            // Already planned by the user: the planned item counts, so it is not added a second time.
            var amount = group.Key.Kind == RecurringKind.Income && usual < latest.Amount ? usual : latest.Amount;
            var planned = list.Any(f => settling.Contains(f.Transaction.Id) || f.Transaction.Source == TransactionSource.Scheduled)
                || rules.Any(r => r.Kind == group.Key.Kind && r.AccountId == group.Key.AccountId && r.ToAccountId == group.Key.ToAccountId
                                  && (r.Amount - amount).Abs().Cents * 100 <= PlannedTolerancePercent * amount.Cents);
            if (planned)
            {
                continue;
            }

            var t = latest.Transaction;
            var name = t.Description.Trim().Length > 0 ? t.Description.Trim()
                : t.CategoryId is { } c && cats.TryGetValue(c, out var category) ? category.Name : string.Empty;
            fixedCosts.Add(new DetectedFixedCost(name, group.Key.Kind, group.Key.AccountId, group.Key.ToAccountId, t.CategoryId, amount, t.Date, monthsSeen));
        }

        // Everything else that is spent is day-to-day spending; settled planned items are counted by the plan itself.
        var dayToDay = flows
            .Where(f => f.Kind == RecurringKind.Expense && !recurringIds.Contains(f.Transaction.Id)
                        && !settling.Contains(f.Transaction.Id) && f.Transaction.Source != TransactionSource.Scheduled)
            .ToList();
        var perMonth = months.Select(m => Money.Sum(dayToDay.Where(f => In(f, m)).Select(f => f.Amount))).ToList();
        var usualMonthly = Max0(Median(perMonth));
        var spent = Money.Sum(dayToDay.Where(f => In(f, thisMonth)).Select(f => f.Amount));
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);
        var pace = Money.FromCents((long)RoundingPolicy.RoundToCents(usualMonthly.Cents * (decimal)(daysInMonth - today.Day) / daysInMonth));
        var rest = usualMonthly - spent > pace ? usualMonthly - spent : pace;


        return new SpendingProfile(
            today, months.Count, from, lastComplete,
            fixedCosts.OrderBy(f => f.Kind == RecurringKind.Income ? 1 : 0).ThenByDescending(f => f.Amount.Cents).ToList(),
            usualMonthly, spent, rest, spendingAccount?.Id);
    }

    /// <summary>
    /// What identifies a counterparty: its description in lower case without digits, punctuation, month names or masked
    /// card numbers, so "Netflix 10/2026 ref 123" and "NETFLIX 11/2026 ref 456" are the same. A line without a
    /// description falls back to its category.
    /// </summary>
    public static string Key(Transaction t)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in t.Description.ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormD) + " ")
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetter(ch))
            {
                sb.Append(ch);
                continue;
            }

            var word = sb.ToString();
            sb.Clear();
            if (word.Length > 1 && !MonthWords.Contains(word) && word.Trim('x').Length > 0)
            {
                words.Add(word);
            }
        }

        return words.Count > 0 ? string.Join(' ', words.Take(12))
            : t.CategoryId is { } category ? "category:" + category.ToString("N")
            : string.Empty;
    }

    private static bool In(Flow f, DateOnly month) => f.Transaction.Date.Year == month.Year && f.Transaction.Date.Month == month.Month;

    private static Money Median(IReadOnlyList<Money> amounts)
    {
        if (amounts.Count == 0)
        {
            return Money.Zero;
        }

        var sorted = amounts.OrderBy(a => a.Cents).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : Money.FromCents((long)RoundingPolicy.RoundToCents((sorted[mid - 1].Cents + sorted[mid].Cents) / 2m));
    }

    private static Money Max0(Money m) => m.IsNegative ? Money.Zero : m;

    /// <summary>One movement of the spendable money. <see cref="Amount"/> is positive for money out (refunds lower it) and for income in.</summary>
    private sealed record Flow(Transaction Transaction, RecurringKind Kind, Guid AccountId, Guid? ToAccountId, Money Amount);
}
