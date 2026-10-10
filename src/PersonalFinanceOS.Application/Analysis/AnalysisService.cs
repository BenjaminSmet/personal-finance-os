using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Analysis;

/// <summary>Spending or income of one category in a month, next to its budget and the user's own history.</summary>
public sealed record CategoryAmount(
    /// <summary>Null for lines without a category.</summary>
    Category? Category,

    /// <summary>Positive. For spending, refunds already lower it.</summary>
    Money Actual,

    /// <summary>The budget for this month, when one is set.</summary>
    Money? Budget,

    /// <summary>Average of the compared earlier months, when there is at least one.</summary>
    Money? PreviousAverage,

    /// <summary>Change against <see cref="PreviousAverage"/> in percent, when that average is not zero.</summary>
    int? ChangePercent,

    /// <summary>Clearly different from the user's own history; only with enough history (spec §13).</summary>
    bool IsUnusual)
{
    public Money? Remaining => Budget is { } b ? b - Actual : null;

    public bool IsOverBudget => Budget is { } b && Actual > b;

    /// <summary>Share of the budget used, in percent.</summary>
    public int? UsedPercent => Budget is { IsPositive: true } b
        ? (int)Math.Round(Actual.Cents * 100m / b.Cents, MidpointRounding.AwayFromZero)
        : null;
}

/// <summary>One month of the trend. Months before the first balance date have no data and are never compared.</summary>
public sealed record MonthPoint(DateOnly Month, bool HasData, bool IsPartial, PeriodTotals Totals);

/// <summary>Spending on an account the user shares, with their own part (spec §13 joint spending/share).</summary>
public sealed record SharedSpending(Account Account, Money Spending, Money PersonalPart);

/// <summary>
/// One month analysed (spec §13, §24 phase 5): income, spending and net, spending per category against budget and
/// history, recurring versus one-off spending, planned versus actual, shared accounts and a short trend.
/// Calculated from the ledger with <see cref="LedgerMath"/>, so it always matches the transaction totals.
/// </summary>
public sealed record MonthAnalysis(
    DateOnly Month,
    DateOnly LastDay,
    bool IsCurrentMonth,

    /// <summary>The earliest balance date of any account: nothing before it is recorded. Null without accounts.</summary>
    DateOnly? HistoryStart,
    PeriodTotals Totals,

    /// <summary>How many complete earlier months <see cref="PreviousAverage"/> is based on (0 to 3).</summary>
    int ComparedMonths,
    PeriodTotals? PreviousAverage,
    IReadOnlyList<CategoryAmount> Expenses,
    IReadOnlyList<CategoryAmount> Income,
    Money BudgetTotal,

    /// <summary>Spending in the categories that have a budget this month.</summary>
    Money BudgetedSpending,

    /// <summary>Spending settled by a planned item (rent, subscriptions…).</summary>
    Money RecurringSpending,
    Money OneOffSpending,
    IReadOnlyList<PlannedOutcome> Planned,
    IReadOnlyList<SharedSpending> Shared,
    IReadOnlyList<MonthPoint> Trend)
{
    public bool HasAccounts => HistoryStart is not null;

    /// <summary>The month starts before the first balance date, so its totals miss earlier spending.</summary>
    public bool IsIncomplete => HistoryStart is { } start && start > Month;

    /// <summary>Unusual changes are only claimed with enough of the user's own history.</summary>
    public bool CanShowUnusual => ComparedMonths >= AnalysisService.UnusualMinimumMonths;

    public IEnumerable<CategoryAmount> OverBudget => Expenses.Where(e => e.IsOverBudget);
}

/// <summary>Budgets and analysis (spec §13): read-only calculations over the ledger, budgets and planned items.</summary>
public sealed class AnalysisService(
    IAccountRepository accounts,
    ICategoryRepository categories,
    ITransactionRepository transactions,
    IBudgetRepository budgets,
    RecurringService recurring,
    IClock clock)
{
    /// <summary>Earlier complete months used for comparisons.</summary>
    public const int ComparisonMonths = 3;

    /// <summary>Months shown in the trend, the analysed month included.</summary>
    public const int TrendMonths = 6;

    /// <summary>Complete earlier months needed before a change is called unusual.</summary>
    public const int UnusualMinimumMonths = 3;

    /// <summary>A category is unusual when it differs this much from its average…</summary>
    public const int UnusualPercent = 50;

    /// <summary>…and by at least this amount, so small categories do not raise alarms.</summary>
    public static readonly Money UnusualMinimum = Money.FromCents(2_500);

    public async Task<MonthAnalysis> AnalyzeMonthAsync(DateOnly month, CancellationToken ct = default)
    {
        var today = clock.Today;
        var first = Budget.MonthOf(month);
        var last = first.AddMonths(1).AddDays(-1);
        var thisMonth = Budget.MonthOf(today);

        var accountList = await accounts.ListAsync(includeArchived: true, ct);
        DateOnly? historyStart = accountList.Count == 0 ? null : accountList.Min(a => a.OpeningDate);
        var trendStart = first.AddMonths(-(TrendMonths - 1));
        var lines = await transactions.ListAsync(new TransactionQuery(From: trendStart, To: last), ct);
        var categoryList = await categories.ListAsync(includeArchived: true, ct);
        var budgetList = await budgets.ListAsync(ct);
        var settling = await recurring.ListSettlingTransactionIdsAsync(ct);

        var totals = LedgerMath.Totals(lines, first, last);

        // Earlier months count only when they are complete: after the first balance date and already over.
        var compared = Enumerable.Range(1, ComparisonMonths)
            .Select(i => first.AddMonths(-i))
            .Where(m => historyStart is { } h && h <= m && m < thisMonth)
            .ToList();
        PeriodTotals? previous = compared.Count == 0
            ? null
            : new PeriodTotals(
                Average(compared.Select(m => LedgerMath.Totals(lines, m, End(m)).Income)),
                Average(compared.Select(m => LedgerMath.Totals(lines, m, End(m)).Expenses)));

        var isCurrent = first == thisMonth;
        var spending = ByCategory(lines, first, last, CategoryKind.Expense);
        var spendingBefore = compared.Select(m => ByCategory(lines, m, End(m), CategoryKind.Expense)).ToList();
        var income = ByCategory(lines, first, last, CategoryKind.Income);
        var incomeBefore = compared.Select(m => ByCategory(lines, m, End(m), CategoryKind.Income)).ToList();

        var expenses = new List<CategoryAmount>();
        var budgetTotal = Money.Zero;
        var budgeted = Money.Zero;
        foreach (var category in categoryList.Where(c => c.Kind == CategoryKind.Expense))
        {
            var actual = spending.GetValueOrDefault(category.Id);
            var budget = category.IsActive ? Budget.AmountFor(budgetList, category.Id, first) : null;
            if (!category.IsActive && actual.IsZero)
            {
                continue;
            }

            if (budget is { } b)
            {
                budgetTotal += b;
                budgeted += actual;
            }

            expenses.Add(Line(category, actual, budget, spendingBefore, isCurrent, compared.Count));
        }

        if (spending.GetValueOrDefault(Guid.Empty) is { IsZero: false } uncategorised)
        {
            expenses.Add(Line(null, uncategorised, null, spendingBefore, isCurrent, compared.Count));
        }

        var incomeLines = new List<CategoryAmount>();
        foreach (var category in categoryList.Where(c => c.Kind == CategoryKind.Income))
        {
            if (income.GetValueOrDefault(category.Id) is { IsZero: false } actual)
            {
                incomeLines.Add(Line(category, actual, null, incomeBefore, isCurrent, compared.Count) with { IsUnusual = false });
            }
        }

        if (income.GetValueOrDefault(Guid.Empty) is { IsZero: false } otherIncome)
        {
            incomeLines.Add(Line(null, otherIncome, null, incomeBefore, isCurrent, compared.Count) with { IsUnusual = false });
        }

        var recurringSpending = Spending(lines.Where(t => settling.Contains(t.Id) || t.Source == TransactionSource.Scheduled), first, last);

        var shared = accountList
            .Where(a => !a.OwnershipShare.IsFull && !a.Type.IsLiability())
            .Select(a => (Account: a, Spent: LedgerMath.Totals(lines.Where(t => t.AccountId == a.Id), first, last).Expenses))
            .Where(x => x.Account.Status == AccountStatus.Active || !x.Spent.IsZero)
            .Select(x => new SharedSpending(x.Account, x.Spent, x.Account.PersonalPart(x.Spent)))
            .ToList();

        var trend = Enumerable.Range(0, TrendMonths)
            .Select(i => trendStart.AddMonths(i))
            .Select(m => new MonthPoint(
                m,
                HasData: historyStart is { } h && End(m) >= h && m <= today,
                IsPartial: historyStart is { } hs && hs > m || m == thisMonth,
                LedgerMath.Totals(lines, m, End(m))))
            .ToList();

        return new MonthAnalysis(
            first,
            last,
            isCurrent,
            historyStart,
            totals,
            compared.Count,
            previous,
            expenses,
            incomeLines,
            budgetTotal,
            budgeted,
            recurringSpending,
            totals.Expenses - recurringSpending,
            await recurring.ListOutcomesAsync(first, last, ct),
            shared,
            trend);
    }

    private static DateOnly End(DateOnly month) => month.AddMonths(1).AddDays(-1);

    private static CategoryAmount Line(Category? category, Money actual, Money? budget, List<Dictionary<Guid, Money>> before, bool isCurrentMonth, int comparedMonths)
    {
        if (comparedMonths == 0)
        {
            return new CategoryAmount(category, actual, budget, null, null, false);
        }

        var key = category?.Id ?? Guid.Empty;
        var average = Average(before.Select(d => d.GetValueOrDefault(key)));
        var difference = actual - average;
        int? change = average.IsPositive
            ? (int)Math.Round(difference.Cents * 100m / average.Cents, MidpointRounding.AwayFromZero)
            : null;

        // A month still in progress can only be unusually high; "lower than usual" would just mean "not over yet".
        var unusual = comparedMonths >= UnusualMinimumMonths
                      && difference.Abs() >= UnusualMinimum
                      && (!isCurrentMonth || difference.IsPositive)
                      && (average.IsZero || difference.Abs().Cents * 100 >= UnusualPercent * average.Cents);
        return new CategoryAmount(category, actual, budget, average, change, unusual);
    }

    /// <summary>
    /// Per category between two dates; lines without a category under <see cref="Guid.Empty"/>. Spending is positive
    /// and lowered by refunds; transfers and adjustments never count (same rules as <see cref="LedgerMath.Totals"/>).
    /// </summary>
    private static Dictionary<Guid, Money> ByCategory(IEnumerable<Transaction> lines, DateOnly from, DateOnly to, CategoryKind kind)
    {
        var result = new Dictionary<Guid, Money>();
        foreach (var t in lines)
        {
            if (!t.IsActive || t.Date < from || t.Date > to || t.Type.CategoryKind() != kind)
            {
                continue;
            }

            var key = t.CategoryId ?? Guid.Empty;
            result[key] = result.GetValueOrDefault(key) + (kind == CategoryKind.Expense ? t.Amount.Negate() : t.Amount);
        }

        return result;
    }

    private static Money Spending(IEnumerable<Transaction> lines, DateOnly from, DateOnly to) => LedgerMath.Totals(lines, from, to).Expenses;

    private static Money Average(IEnumerable<Money> amounts)
    {
        var list = amounts.ToList();
        return list.Count == 0
            ? Money.Zero
            : Money.FromCents((long)RoundingPolicy.RoundToCents(Money.Sum(list).Cents / (decimal)list.Count));
    }
}
