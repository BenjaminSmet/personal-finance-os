using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Assets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Cards;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Ownership;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Reports;

/// <summary>
/// Builds the reports of spec §18 from the ledger and the calculation services. Every total comes from
/// <see cref="LedgerMath"/>, so a report reconciles with the ledger. Planned items and projections are only shown when
/// asked for, in their own tables and labelled as estimates; they are never added to actual totals. Generating a report
/// only reads data.
/// </summary>
public sealed class ReportService(
    IAccountRepository accounts,
    ICategoryRepository categories,
    ITransactionRepository transactions,
    IBudgetRepository budgets,
    IReconciliationRepository reconciliations,
    IOwnershipRepository ownership,
    RecurringService recurring,
    ForecastService forecast,
    CreditCardService cards,
    NetWorthService netWorth,
    IClock clock)
{
    /// <summary>The longest period a report covers, so a typing mistake in a year cannot run for minutes.</summary>
    public const int MaxYears = 10;

    /// <summary>Earlier complete months a monthly summary compares with (as on the analysis page).</summary>
    public const int ComparisonMonths = 3;

    public async Task<Report> GenerateAsync(ReportRequest request, CancellationToken ct = default)
    {
        if (request.From > request.To)
        {
            throw new DomainValidationException("report.period.invalid");
        }

        if (request.To > request.From.AddYears(MaxYears))
        {
            throw new DomainValidationException("report.period.tooLong");
        }

        var data = await LoadAsync(request, ct);
        var (title, notes, tables) = request.Type switch
        {
            ReportType.MonthlySummary => MonthlySummary(data),
            ReportType.CashFlow => await CashFlowAsync(data, ct),
            ReportType.Account => await AccountAsync(data, ct),
            ReportType.Budget => BudgetReport(data),
            ReportType.JointFinance => Joint(data),
            ReportType.CreditCard => await CardAsync(data, ct),
            ReportType.NetWorth => await NetWorthAsync(data, ct),
            _ => Annual(data),
        };

        if (request.CategoryIds is { Count: > 0 } && request.Type is ReportType.MonthlySummary or ReportType.Budget or ReportType.AnnualOverview)
        {
            notes.Add(T("Report_Note_CategoryFilter", request.CategoryIds.Count));
        }

        if (request.AccountIds is { Count: > 0 } && request.Type is ReportType.MonthlySummary or ReportType.AnnualOverview or ReportType.CashFlow)
        {
            notes.Add(T("Report_Note_AccountFilter", string.Join(", ", data.SelectedAccounts.Select(a => a.Name))));
        }

        return new Report(request, title, request.From, request.To, clock.Now, notes, tables);
    }

    // ===== 1. Monthly financial summary =====

    private (ReportText, List<ReportText>, List<ReportTable>) MonthlySummary(Data d)
    {
        var notes = new List<ReportText>();
        var months = Months(d, notes);
        var rows = months.Select(m =>
        {
            var t = d.Totals(m, End(m));
            return Row([new ReportMonth(m), t.Income, t.Expenses, t.Net, PartialNote(d, m)]);
        }).ToList();
        if (months.Count > 1)
        {
            var total = d.Totals(months[0], End(months[^1]));
            rows.Add(Total([T("Report_Total"), total.Income, total.Expenses, total.Net, null]));
        }

        var tables = new List<ReportTable>
        {
            new(T("Report_Table_PerMonth"), [Col("Report_Col_Month"), Num("Report_Col_Income"), Num("Report_Col_Spending"), Num("Report_Col_Net"), Col("Report_Col_Remark")], rows),
        };

        // The same complete months the analysis page compares with: before the period, after the first balance date.
        var compared = Enumerable.Range(1, ComparisonMonths)
            .Select(i => (months.Count > 0 ? months[0] : MonthOf(d.Request.From)).AddMonths(-i))
            .Where(m => d.HistoryStart is { } h && h <= m)
            .OrderBy(m => m)
            .ToList();
        if (months.Count > 0)
        {
            var from = months[0];
            var to = End(months[^1]);
            tables.Add(CategoryTable(d, CategoryKind.Expense, from, to, months.Count, compared, "Report_Table_SpendingByCategory"));
            tables.Add(CategoryTable(d, CategoryKind.Income, from, to, months.Count, compared, "Report_Table_IncomeByCategory"));
            if (compared.Count > 0)
            {
                var before = d.Totals(compared[0], End(compared[^1]));
                var now = d.Totals(from, to);
                tables.Add(new ReportTable(
                    T("Report_Table_Comparison", compared.Count),
                    [Col("Report_Col_Item"), Num("Report_Col_PerMonthPeriod"), Num("Report_Col_PerMonthBefore"), Num("Report_Col_Difference")],
                    [
                        Compare("Report_Col_Income", Avg(now.Income, months.Count), Avg(before.Income, compared.Count)),
                        Compare("Report_Col_Spending", Avg(now.Expenses, months.Count), Avg(before.Expenses, compared.Count)),
                        Compare("Report_Col_Net", Avg(now.Net, months.Count), Avg(before.Net, compared.Count)),
                    ]));
            }
            else
            {
                notes.Add(T("Report_Note_NoComparison"));
            }
        }

        AddPlanned(d, tables, notes);
        return (T("ReportType_MonthlySummary"), notes, tables);

        static ReportRow Compare(string key, Money now, Money before) => Row([T(key), now, before, now - before]);
    }

    private static ReportTable CategoryTable(Data d, CategoryKind kind, DateOnly from, DateOnly to, int monthCount, IReadOnlyList<DateOnly> compared, string titleKey)
    {
        var lines = d.Lines(from, to).Where(t => t.Type.CategoryKind() == kind).ToList();
        var before = compared.Count == 0 ? [] : d.Lines(compared[0], End(compared[^1])).Where(t => t.Type.CategoryKind() == kind).ToList();
        var total = Money.Zero;
        var rows = new List<ReportRow>();
        foreach (var group in lines.GroupBy(t => t.CategoryId).OrderByDescending(g => Sum(g, kind).Cents))
        {
            var amount = Sum(group, kind);
            total += amount;
            var name = group.Key is { } id && d.Categories.TryGetValue(id, out var c) ? (object)c.Name : T("Report_Uncategorised");
            rows.Add(Row(compared.Count == 0
                ? [name, amount, Avg(amount, monthCount)]
                : [name, amount, Avg(amount, monthCount), Avg(Sum(before.Where(t => t.CategoryId == group.Key), kind), compared.Count)]));
        }

        if (rows.Count > 0)
        {
            rows.Add(Total(compared.Count == 0
                ? [T("Report_Total"), total, Avg(total, monthCount)]
                : [T("Report_Total"), total, Avg(total, monthCount), Avg(Sum(before, kind), compared.Count)]));
        }

        List<ReportColumn> columns = [Col("Report_Col_Category"), Num("Report_Col_Amount"), Num("Report_Col_PerMonth")];
        if (compared.Count > 0)
        {
            columns.Add(Num("Report_Col_PerMonthBefore"));
        }

        return new ReportTable(T(titleKey), columns, rows);
    }

    // ===== 2. Cash flow =====

    private async Task<(ReportText, List<ReportText>, List<ReportTable>)> CashFlowAsync(Data d, CancellationToken ct)
    {
        var notes = new List<ReportText>();
        var today = clock.Today;
        var from = d.Request.From;
        var to = d.Request.To;
        var actualTo = to < today ? to : today;
        var list = d.SelectedAccounts.Where(a => a.Type != AccountType.Pension && a.Type != AccountType.Investment).ToList();
        var rows = new List<ReportRow>();
        foreach (var a in list)
        {
            var opening = Balance(d, a, from.AddDays(-1));
            var period = actualTo >= from ? d.AllLines.Where(t => t.AccountId == a.Id && t.Date >= from && t.Date <= actualTo).ToList() : [];
            var moneyIn = Money.Sum(period.Where(t => t.Amount.IsPositive).Select(t => t.Amount));
            var moneyOut = Money.Sum(period.Where(t => t.Amount.IsNegative).Select(t => t.Amount)).Negate();
            rows.Add(Row([a.Name, opening, moneyIn, moneyOut, Balance(d, a, actualTo < from ? from.AddDays(-1) : actualTo)]));
        }

        var tables = new List<ReportTable>
        {
            new(T("Report_Table_CashPerAccount", actualTo < from ? from.AddDays(-1) : actualTo),
                [Col("Report_Col_Account"), Num("Report_Col_Opening"), Num("Report_Col_In"), Num("Report_Col_Out"), Num("Report_Col_Closing")], rows,
                T("Report_Note_TransfersIncluded")),
        };

        if (to > today)
        {
            if (d.Request.IncludePlanned)
            {
                var timeline = await forecast.TimelineAsync(to, ct);
                var ids = list.Select(a => a.Id).ToHashSet();
                var events = timeline.Events.Where(e => ids.Contains(e.AccountId) && e.Date <= to && e.Date >= today).OrderBy(e => e.Date).ToList();
                var projected = list.Where(a => timeline.Accounts.Any(x => x.Id == a.Id))
                    .Select(a =>
                    {
                        var own = events.Where(e => e.AccountId == a.Id).ToList();
                        return Row([
                            a.Name,
                            Balance(d, a, today),
                            Money.Sum(own.Where(e => e.Amount.IsPositive).Select(e => e.Amount)),
                            Money.Sum(own.Where(e => e.Amount.IsNegative && e.Kind != CashEventKind.CardSettlement).Select(e => e.Amount)).Negate(),
                            Money.Sum(own.Where(e => e.Kind == CashEventKind.CardSettlement).Select(e => e.Amount)).Negate(),
                            timeline.ForAccount(a.Id).BalanceOn(to),
                        ], RowStyle.Subtle);
                    })
                    .ToList();
                tables.Add(new ReportTable(
                    T("Report_Table_Projection", to),
                    [Col("Report_Col_Account"), Num("Report_Col_Today"), Num("Report_Col_PlannedIn"), Num("Report_Col_PlannedOut"), Num("Report_Col_CardStatements"), Num("Report_Col_ProjectedClosing")],
                    projected,
                    T("Report_Note_Projection")));
                tables.Add(new ReportTable(
                    T("Report_Table_Commitments"),
                    [Col("Report_Col_Date"), Col("Report_Col_Item"), Col("Report_Col_Account"), Col("Report_Col_Kind"), Num("Report_Col_Amount")],
                    events.Select(e => Row([e.Date, e.Name ?? string.Empty, d.Accounts[e.AccountId].Name, T("Report_Event_" + e.Kind), e.Amount], RowStyle.Subtle)).ToList(),
                    T("Report_Note_Estimates")));
                notes.AddRange(timeline.Caveats.Select(c => T("ForecastCaveat_" + c)));
                if (list.Any(a => timeline.Accounts.All(x => x.Id != a.Id)))
                {
                    notes.Add(T("Report_Note_CardsNotProjected"));
                }
            }
            else
            {
                notes.Add(T("Report_Note_FutureWithoutPlanned"));
            }
        }

        notes.Insert(0, T("Report_Note_CashFlowActual"));
        return (T("ReportType_CashFlow"), notes, tables);
    }

    // ===== 3. Account =====

    private async Task<(ReportText, List<ReportText>, List<ReportTable>)> AccountAsync(Data d, CancellationToken ct)
    {
        if (d.Request.AccountIds is not { Count: > 0 } ids || !d.Accounts.TryGetValue(ids[0], out var account))
        {
            throw new DomainValidationException("report.account.required");
        }

        var notes = new List<ReportText>();
        var from = d.Request.From;
        var to = d.Request.To;
        var opening = Balance(d, account, from.AddDays(-1));
        var lines = d.AllLines.Where(t => t.AccountId == account.Id && t.Date >= from && t.Date <= to)
            .OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
        var running = opening;
        var rows = new List<ReportRow> { Row([null, T("Report_OpeningBalance"), null, null, null, opening], RowStyle.Subtle) };
        foreach (var t in lines)
        {
            running += t.Amount;
            var counterpart = t.TransferId is { } tid ? d.AllLines.FirstOrDefault(x => x.TransferId == tid && x.Id != t.Id) : null;
            var other = counterpart is not null && d.Accounts.TryGetValue(counterpart.AccountId, out var o) ? o : null;
            var category = t.CategoryId is { } c && d.Categories.TryGetValue(c, out var cat) ? cat.Name : other?.Name;
            rows.Add(Row([t.Date, t.Description, category, T("EntryKind_" + LedgerService.KindOf(t, account, other)), t.Amount, running]));
        }

        rows.Add(Total([null, T("Report_ClosingBalance"), null, null, Money.Sum(lines.Select(t => t.Amount)), running]));
        var totals = LedgerMath.Totals(lines, from, to);
        var tables = new List<ReportTable>
        {
            new(T("Report_Table_Summary"), [Col("Report_Col_Item"), Num("Report_Col_Amount")],
            [
                Row([T("Report_OpeningBalance"), opening]),
                Row([T("Report_Col_In"), Money.Sum(lines.Where(t => t.Amount.IsPositive).Select(t => t.Amount))]),
                Row([T("Report_Col_Out"), Money.Sum(lines.Where(t => t.Amount.IsNegative).Select(t => t.Amount)).Negate()]),
                Row([T("Report_ClosingBalance"), running], RowStyle.Total),
                Row([T("Report_Col_Income"), totals.Income]),
                Row([T("Report_Col_Spending"), totals.Expenses]),
            ], T("Report_Note_InOutVsIncome")),
            new(T("Report_Table_Transactions", lines.Count),
                [Col("Report_Col_Date"), Col("Report_Col_Description"), Col("Report_Col_Category"), Col("Report_Col_Kind"), Num("Report_Col_Amount"), Num("Report_Col_Balance")], rows),
        };

        var recs = (await reconciliations.ListAsync(account.Id, ct)).OrderBy(r => r.StatementDate).ToList();
        tables.Add(new ReportTable(
            T("Report_Table_Reconciliation"),
            [Col("Report_Col_StatementDate"), Num("Report_Col_BankBalance"), Num("Report_Col_AppBalance"), Num("Report_Col_Difference"), Col("Report_Col_Status")],
            recs.Where(r => r.StatementDate <= to)
                .Select(r => Row([r.StatementDate, r.StatementBalance, r.CalculatedBalance, r.Difference, T(r.Difference.IsZero ? "Report_Rec_Matched" : r.AdjustmentTransactionId is not null ? "Report_Rec_Adjusted" : "Report_Rec_Difference")]))
                .ToList()));
        var last = recs.LastOrDefault(r => r.StatementDate <= to);
        notes.Add(last is null
            ? T("Report_Note_NotReconciled")
            : T(last.IsMatched ? "Report_Note_ReconciledOn" : "Report_Note_ReconciledDifference", last.StatementDate, last.Difference));
        if (account.Type.IsLiability())
        {
            notes.Add(T("Report_Note_CardBalance"));
        }

        if (from < account.OpeningDate)
        {
            notes.Add(T("Report_Note_OpenedOn", account.OpeningDate));
        }

        AddPlanned(d, tables, notes, [account.Id]);
        return (T("Report_Title_Account", account.Name), notes, tables);
    }

    // ===== 4. Budget =====

    private (ReportText, List<ReportText>, List<ReportTable>) BudgetReport(Data d)
    {
        var notes = new List<ReportText>();
        var months = Months(d, notes);
        var spending = d.Categories.Values.Where(c => c.Kind == CategoryKind.Expense && d.CategoryIncluded(c.Id)).OrderBy(c => c.SortOrder).ToList();
        var rows = new List<ReportRow>();
        var totalBudget = Money.Zero;
        var totalBudgeted = Money.Zero;
        var unbudgeted = Money.Zero;
        foreach (var c in spending)
        {
            var budget = months.Select(m => Domain.Budgets.Budget.AmountFor(d.Budgets, c.Id, m)).ToList();
            var actual = months.Count == 0 ? Money.Zero : Sum(d.Lines(months[0], End(months[^1])).Where(t => t.CategoryId == c.Id && t.Type.CategoryKind() == CategoryKind.Expense), CategoryKind.Expense);
            if (budget.All(b => b is null))
            {
                unbudgeted += actual;
                if (!actual.IsZero)
                {
                    rows.Add(Row([c.Name, null, actual, null, T("Report_NoBudget")], RowStyle.Subtle));
                }

                continue;
            }

            // Months without a budget for this category count with their spending only.
            var b = Money.Sum(budget.Select(x => x ?? Money.Zero));
            var budgetedActual = Money.Sum(months.Where((m, i) => budget[i] is not null)
                .Select(m => Sum(d.Lines(m, End(m)).Where(t => t.CategoryId == c.Id && t.Type.CategoryKind() == CategoryKind.Expense), CategoryKind.Expense)));
            totalBudget += b;
            totalBudgeted += budgetedActual;
            unbudgeted += actual - budgetedActual;
            var left = b - budgetedActual;
            rows.Add(Row([c.Name, b, budgetedActual, left, left.IsNegative ? T("Report_Over", left.Negate()) : T("Report_Used", Percent(budgetedActual, b))]));
        }

        var uncategorised = months.Count == 0 ? Money.Zero : Sum(d.Lines(months[0], End(months[^1])).Where(t => t.CategoryId is null && t.Type.CategoryKind() == CategoryKind.Expense), CategoryKind.Expense);
        if (d.Request.CategoryIds is null && !uncategorised.IsZero)
        {
            unbudgeted += uncategorised;
            rows.Add(Row([T("Report_Uncategorised"), null, uncategorised, null, T("Report_NoBudget")], RowStyle.Subtle));
        }

        rows.Add(Total([T("Report_TotalBudgeted"), totalBudget, totalBudgeted, totalBudget - totalBudgeted, null]));
        rows.Add(Total([T("Report_TotalUnbudgeted"), null, unbudgeted, null, null]));
        var tables = new List<ReportTable>
        {
            new(T("Report_Table_BudgetVsActual"), [Col("Report_Col_Category"), Num("Report_Col_Budget"), Num("Report_Col_Actual"), Num("Report_Col_Left"), Col("Report_Col_Status")], rows),
        };
        if (months.Count > 1)
        {
            tables.Add(new ReportTable(
                T("Report_Table_PerMonth"),
                [Col("Report_Col_Month"), Num("Report_Col_Budget"), Num("Report_Col_Actual"), Num("Report_Col_Left")],
                months.Select(m =>
                {
                    var withBudget = spending.Where(c => Domain.Budgets.Budget.AmountFor(d.Budgets, c.Id, m) is not null).ToList();
                    var budget = Money.Sum(withBudget.Select(c => Domain.Budgets.Budget.AmountFor(d.Budgets, c.Id, m)!.Value));
                    var actual = Sum(d.Lines(m, End(m)).Where(t => withBudget.Any(c => c.Id == t.CategoryId) && t.Type.CategoryKind() == CategoryKind.Expense), CategoryKind.Expense);
                    return Row([new ReportMonth(m), budget, actual, budget - actual]);
                }).ToList()));
        }

        notes.Add(T("Report_Note_Budget"));
        return (T("ReportType_Budget"), notes, tables);
    }

    // ===== 5. Joint finances =====

    private (ReportText, List<ReportText>, List<ReportTable>) Joint(Data d)
    {
        var notes = new List<ReportText> { T("Report_Note_Joint") };
        var shared = d.SelectedAccounts
            .Where(a => !a.Type.IsLiability() && (a.Type == AccountType.JointCurrent || d.Ownership.Any(o => o.AccountId == a.Id) || !a.OwnershipShare.IsFull))
            .ToList();
        var tables = new List<ReportTable>();
        if (shared.Count == 0)
        {
            notes.Add(T("Report_Note_NoJoint"));
        }

        var from = d.Request.From;
        var to = d.Request.To;
        foreach (var a in shared)
        {
            var lines = d.AllLines.Where(t => t.AccountId == a.Id && t.Date >= from && t.Date <= to).OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
            var rows = new List<ReportRow>();
            var yourIn = Money.Zero;
            var yourOut = Money.Zero;
            foreach (var t in lines)
            {
                var share = OwnershipHistory.ShareAt(d.Ownership, a.Id, a.OwnershipShare, t.Date);
                var part = share.ApplyTo(t.Amount);
                if (part.IsPositive)
                {
                    yourIn += part;
                }
                else
                {
                    yourOut -= part;
                }

                var category = t.CategoryId is { } c && d.Categories.TryGetValue(c, out var cat) ? cat.Name : null;
                rows.Add(Row([t.Date, t.Description, category, t.Amount, share, part]));
            }

            var fullIn = Money.Sum(lines.Where(t => t.Amount.IsPositive).Select(t => t.Amount));
            var fullOut = Money.Sum(lines.Where(t => t.Amount.IsNegative).Select(t => t.Amount)).Negate();
            var totals = LedgerMath.Totals(lines, from, to);
            var shareNow = OwnershipHistory.ShareAt(d.Ownership, a.Id, a.OwnershipShare, to);
            tables.Add(new ReportTable(
                T("Report_Table_JointSummary", a.Name),
                [Col("Report_Col_Item"), Num("Report_Col_Full"), Num("Report_Col_YourPart")],
                [
                    Row([T("Report_OpeningBalance"), Balance(d, a, from.AddDays(-1)), OwnershipHistory.ShareAt(d.Ownership, a.Id, a.OwnershipShare, from.AddDays(-1)).ApplyTo(Balance(d, a, from.AddDays(-1)))]),
                    Row([T("Report_Col_In"), fullIn, yourIn]),
                    Row([T("Report_Col_Out"), fullOut, yourOut]),
                    Row([T("Report_Col_Spending"), totals.Expenses, null]),
                    Row([T("Report_ClosingBalance"), Balance(d, a, to), shareNow.ApplyTo(Balance(d, a, to))], RowStyle.Total),
                ],
                T("Report_Note_ShareOn", shareNow, to)));
            tables.Add(new ReportTable(
                T("Report_Table_JointActivity", a.Name),
                [Col("Report_Col_Date"), Col("Report_Col_Description"), Col("Report_Col_Category"), Num("Report_Col_Full"), Num("Report_Col_Share"), Num("Report_Col_YourPart")],
                rows));
        }

        return (T("ReportType_JointFinance"), notes, tables);
    }

    // ===== 6. Credit card =====

    private async Task<(ReportText, List<ReportText>, List<ReportTable>)> CardAsync(Data d, CancellationToken ct)
    {
        var notes = new List<ReportText> { T("Report_Note_CardNoDoubleCount") };
        var today = clock.Today;
        var statuses = (await cards.ListStatusesAsync(ct)).ToDictionary(s => s.Card.Id);
        var list = d.SelectedAccounts.Where(a => a.Type.IsLiability()).ToList();
        var tables = new List<ReportTable>();
        if (list.Count == 0)
        {
            notes.Add(T("Report_Note_NoCards"));
        }

        foreach (var card in list)
        {
            var status = statuses.GetValueOrDefault(card.Id);
            if (status?.Profile is not { } profile)
            {
                notes.Add(T("Report_Note_CardNotSetUp", card.Name));
                continue;
            }

            var lines = d.AllLines.Where(t => t.AccountId == card.Id).ToList();
            var rows = new List<ReportRow>();
            for (var period = profile.PeriodOf(d.Request.From); period.Start <= d.Request.To; period = StatementCycle.Next(period, profile.StatementStartDay))
            {
                if (period.Start > today)
                {
                    break;
                }

                var inPeriod = lines.Where(t => period.Contains(t.Date)).ToList();
                var purchases = Money.Sum(inPeriod.Where(t => t.Type == TransactionType.Expense).Select(t => t.Amount)).Negate();
                var refunds = Money.Sum(inPeriod.Where(t => t.Type == TransactionType.Refund).Select(t => t.Amount));
                var next = StatementCycle.Next(period, profile.StatementStartDay);
                var paid = Money.Sum(lines.Where(t => t.IsTransfer && t.Amount.IsPositive && t.Date > period.End && t.Date <= next.End && t.Date <= today).Select(t => t.Amount));
                var closed = period.End < today;
                var statement = closed ? Max0(LedgerMath.Balance(card, lines, period.End).Negate()) : status.EstimatedStatement;
                var remaining = Max0(statement - paid);
                rows.Add(Row(
                    [T("Report_Period", period.Start, period.End), purchases, refunds, statement, T(closed ? "Report_Basis_Actual" : "Report_Basis_Estimate"), closed ? paid : null, closed ? remaining : null, profile.SettlementDateOf(period)],
                    closed ? RowStyle.Normal : RowStyle.Subtle));
            }

            tables.Add(new ReportTable(
                T("Report_Table_Statements", card.Name),
                [Col("Report_Col_Period"), Num("Report_Col_Purchases"), Num("Report_Col_Refunds"), Num("Report_Col_Statement"), Col("Report_Col_Basis"), Num("Report_Col_Paid"), Num("Report_Col_Remaining"), Col("Report_Col_DebitDate")],
                rows,
                T("Report_Note_StatementEstimate")));
            tables.Add(new ReportTable(
                T("Report_Table_CardNow", card.Name),
                [Col("Report_Col_Item"), Num("Report_Col_Amount")],
                [
                    Row([T("Report_CreditLimit"), profile.CreditLimit]),
                    Row([T("Report_Outstanding"), status.Outstanding]),
                    Row([T("Report_AvailableCredit"), status.AvailableCredit]),
                ],
                T("Report_Note_CreditNotCash")));
            var purchasesTable = lines.Where(t => t.Date >= d.Request.From && t.Date <= d.Request.To && !(t.IsTransfer && t.Amount.IsPositive))
                .OrderBy(t => t.Date).ThenBy(t => t.CreatedAt)
                .Select(t => Row([t.Date, t.Description, t.CategoryId is { } c && d.Categories.TryGetValue(c, out var cat) ? cat.Name : null, t.Amount.Negate(), T("Report_Period", profile.PeriodOf(t.Date).Start, profile.PeriodOf(t.Date).End)]))
                .ToList();
            tables.Add(new ReportTable(
                T("Report_Table_CardLines", card.Name),
                [Col("Report_Col_Date"), Col("Report_Col_Description"), Col("Report_Col_Category"), Num("Report_Col_Amount"), Col("Report_Col_Period")],
                purchasesTable,
                T("Report_Note_CardLines")));
        }

        return (T("ReportType_CreditCard"), notes, tables);
    }

    // ===== 7. Net worth =====

    private async Task<(ReportText, List<ReportText>, List<ReportTable>)> NetWorthAsync(Data d, CancellationToken ct)
    {
        var notes = new List<ReportText>();
        var today = clock.Today;
        var asOf = d.Request.To < today ? d.Request.To : today;
        var worth = await netWorth.CalculateAsync(asOf == today ? null : asOf, ct);
        var rows = worth.Lines
            .OrderBy(l => l.IsLiability)
            .ThenBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => Row([
                l.Name,
                T(l.IsLiability ? "Report_Liability" : "Report_Asset"),
                l.IsLiability ? l.FullValue.Negate() : l.FullValue,
                l.Share,
                l.IsLiability ? l.PersonalValue.Negate() : l.PersonalValue,
                T("ValueSource_" + l.Source),
                l.ValuedOn,
            ], l.IsUncertain ? RowStyle.Subtle : RowStyle.Normal))
            .ToList();
        rows.Add(Total([T("Report_TotalAssets"), null, null, null, worth.Assets, null, null]));
        rows.Add(Total([T("Report_TotalLiabilities"), null, null, null, worth.Liabilities.Negate(), null, null]));
        rows.Add(Total([T("Report_NetWorth"), null, worth.HasSharedItems ? worth.HouseholdTotal : null, null, worth.Total, null, null]));
        var tables = new List<ReportTable>
        {
            new(T("Report_Table_NetWorthOn", asOf),
                [Col("Report_Col_Item"), Col("Report_Col_Kind"), Num("Report_Col_Full"), Num("Report_Col_Share"), Num("Report_Col_YourPart"), Col("Report_Col_Source"), Col("Report_Col_ValuedOn")],
                rows,
                T("Report_Note_NetWorthLines")),
        };

        var first = MonthOf(d.Request.From);
        var span = ((today.Year - first.Year) * 12) + today.Month - first.Month + 1;
        if (span > 1)
        {
            var history = await netWorth.HistoryAsync(Math.Min(span, MaxYears * 12), ct);
            var points = history.Points.Where(p => p.Month >= first && p.Date <= d.Request.To).ToList();
            var historyRows = new List<ReportRow>();
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i];
                historyRows.Add(Row([new ReportMonth(p.Month), p.Date, p.Total, i == 0 ? null : p.Total - points[i - 1].Total, p.IsComplete ? null : T("Report_Incomplete")]));
            }

            tables.Add(new ReportTable(
                T("Report_Table_NetWorthHistory"),
                [Col("Report_Col_Month"), Col("Report_Col_Date"), Num("Report_Col_NetWorth"), Num("Report_Col_Change"), Col("Report_Col_Remark")],
                historyRows));
        }

        if (!worth.IsComplete)
        {
            notes.Add(T("Report_Note_NetWorthIncomplete"));
        }

        if (worth.HasStaleValues)
        {
            notes.Add(T("Report_Note_StaleValues", NetWorthService.StaleAfterDays));
        }

        notes.Add(T("Report_Note_NoCreditLimit"));
        return (T("ReportType_NetWorth"), notes, tables);
    }

    // ===== 8. Annual overview =====

    private (ReportText, List<ReportText>, List<ReportTable>) Annual(Data d)
    {
        var notes = new List<ReportText>();
        var months = Months(d, notes);
        var tables = new List<ReportTable>();
        foreach (var year in months.GroupBy(m => m.Year))
        {
            var list = year.ToList();
            var rows = list.Select(m =>
            {
                var t = d.Totals(m, End(m));
                return Row([new ReportMonth(m), t.Income, t.Expenses, t.Net, PartialNote(d, m)]);
            }).ToList();
            var total = d.Totals(list[0], End(list[^1]));
            rows.Add(Total([T("Report_YearTotal", year.Key), total.Income, total.Expenses, total.Net, null]));
            rows.Add(Row([T("Report_MonthAverage", list.Count), Avg(total.Income, list.Count), Avg(total.Expenses, list.Count), Avg(total.Net, list.Count), null], RowStyle.Subtle));
            tables.Add(new ReportTable(T("Report_Table_Year", year.Key), [Col("Report_Col_Month"), Num("Report_Col_Income"), Num("Report_Col_Spending"), Num("Report_Col_Net"), Col("Report_Col_Remark")], rows));
            tables.Add(CategoryTable(d, CategoryKind.Expense, list[0], End(list[^1]), list.Count, [], "Report_Table_SpendingByCategory") with
            {
                Title = T("Report_Table_SpendingByCategoryYear", year.Key),
            });
        }

        AddPlanned(d, tables, notes);
        return (T("ReportType_AnnualOverview"), notes, tables);
    }

    // ===== Shared pieces =====

    /// <summary>
    /// The months of the period that have data: from the first balance date and not after today. Months left out are
    /// named in a note, so an incomplete history is never compared as if it were complete.
    /// </summary>
    private List<DateOnly> Months(Data d, List<ReportText> notes)
    {
        var today = clock.Today;
        var all = new List<DateOnly>();
        for (var m = MonthOf(d.Request.From); m <= d.Request.To; m = m.AddMonths(1))
        {
            all.Add(m);
        }

        var shown = all.Where(m => d.HistoryStart is { } h && End(m) >= h && m <= today).ToList();
        var before = all.Where(m => d.HistoryStart is not { } h || End(m) < h).ToList();
        if (before.Count > 0)
        {
            notes.Add(d.HistoryStart is { } h ? T("Report_Note_BeforeHistory", h) : T("Report_Note_NoAccounts"));
        }

        if (all.Any(m => m > today))
        {
            notes.Add(T("Report_Note_FutureMonths"));
        }

        return shown;
    }

    private object? PartialNote(Data d, DateOnly month)
    {
        var today = clock.Today;
        if (MonthOf(today) == month)
        {
            return T("Report_UntilToday", today);
        }

        return d.HistoryStart is { } h && h > month ? T("Report_FromDate", h) : null;
    }

    /// <summary>Planned items in the rest of the period, in their own table, when the user asked for them.</summary>
    private void AddPlanned(Data d, List<ReportTable> tables, List<ReportText> notes, IReadOnlyList<Guid>? accountIds = null)
    {
        var today = clock.Today;
        if (d.Request.To < today)
        {
            return;
        }

        if (!d.Request.IncludePlanned)
        {
            notes.Add(T("Report_Note_PlannedExcluded"));
            return;
        }

        var accountsWanted = accountIds ?? d.Request.AccountIds;
        var start = d.Request.From > today ? d.Request.From : today;
        var rows = d.Open
            .Where(o => o.DueDate <= d.Request.To && (o.DueDate >= start || o.IsOverdue))
            .Where(o => accountsWanted is not { Count: > 0 } || accountsWanted.Contains(o.Rule.AccountId) || (o.Rule.ToAccountId is { } t && accountsWanted.Contains(t)))
            .Where(o => d.Request.CategoryIds is not { Count: > 0 } || (o.Rule.CategoryId is { } c && d.Request.CategoryIds.Contains(c)))
            .OrderBy(o => o.DueDate)
            .Select(o => Row([
                o.DueDate,
                o.Rule.Name,
                d.Accounts.TryGetValue(o.Rule.AccountId, out var a) ? a.Name : null,
                T("RecurringKind_" + o.Rule.Kind),
                o.Rule.Kind == RecurringKind.Expense ? o.Amount.Negate() : o.Amount,
                o.IsOverdue ? T("Report_Overdue") : null,
            ], RowStyle.Subtle))
            .ToList();
        tables.Add(new ReportTable(
            T("Report_Table_Planned"),
            [Col("Report_Col_Date"), Col("Report_Col_Item"), Col("Report_Col_Account"), Col("Report_Col_Kind"), Num("Report_Col_Amount"), Col("Report_Col_Remark")],
            rows,
            T("Report_Note_PlannedSeparate")));
    }

    private async Task<Data> LoadAsync(ReportRequest request, CancellationToken ct)
    {
        var accountList = await accounts.ListAsync(includeArchived: true, ct);
        var categoryList = await categories.ListAsync(includeArchived: true, ct);
        var lines = await transactions.ListAsync(new TransactionQuery(To: request.To), ct);
        var open = request.IncludePlanned && request.To >= clock.Today ? await recurring.ListOpenAsync(request.To, withMatches: false, ct) : [];
        return new Data(
            request,
            accountList.ToDictionary(a => a.Id),
            categoryList.ToDictionary(c => c.Id),
            lines.Where(t => t.IsActive).ToList(),
            await budgets.ListAsync(ct),
            await ownership.ListAsync(ct),
            open,
            accountList.Count == 0 ? null : accountList.Min(a => a.OpeningDate));
    }

    private static Money Balance(Data d, Account account, DateOnly asOf) => LedgerMath.Balance(account, d.AllLines, asOf);

    private static Money Sum(IEnumerable<Transaction> lines, CategoryKind kind) =>
        kind == CategoryKind.Income ? Money.Sum(lines.Select(t => t.Amount)) : Money.Sum(lines.Select(t => t.Amount)).Negate();

    private static Money Avg(Money total, int count) =>
        count <= 0 ? Money.Zero : Money.FromCents((long)RoundingPolicy.RoundToCents(total.Cents / (decimal)count));

    private static ReportPercent Percent(Money part, Money whole) =>
        new(whole.IsZero ? 0 : (int)Math.Round(part.Cents * 100m / whole.Cents, MidpointRounding.AwayFromZero));

    private static Money Max0(Money m) => m.IsNegative ? Money.Zero : m;

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateOnly End(DateOnly month) => month.AddMonths(1).AddDays(-1);

    private static ReportText T(string key, params object[] args) => new(key, args);

    private static ReportColumn Col(string key) => new(T(key));

    private static ReportColumn Num(string key) => new(T(key), ColumnAlign.Right);

    private static ReportRow Row(object?[] cells, RowStyle style = RowStyle.Normal) => new(cells, style);

    private static ReportRow Total(object?[] cells) => new(cells, RowStyle.Total);

    /// <summary>Everything a report reads, loaded once, with the user's account and category filters applied.</summary>
    private sealed record Data(
        ReportRequest Request,
        IReadOnlyDictionary<Guid, Account> Accounts,
        IReadOnlyDictionary<Guid, Category> Categories,
        IReadOnlyList<Transaction> AllLines,
        IReadOnlyList<Budget> Budgets,
        IReadOnlyList<OwnershipChange> Ownership,
        IReadOnlyList<PlannedOccurrence> Open,
        DateOnly? HistoryStart)
    {
        public IReadOnlyList<Account> SelectedAccounts { get; } = Accounts.Values
            .Where(a => Request.AccountIds is not { Count: > 0 } ids ? a.OpeningDate <= Request.To : ids.Contains(a.Id))
            .OrderBy(a => a.SortOrder)
            .ToList();

        public bool CategoryIncluded(Guid? id) => Request.CategoryIds is not { Count: > 0 } ids || (id is { } x && ids.Contains(x));

        /// <summary>Lines in the period on the selected accounts, with the category filter applied to income and spending.</summary>
        public IEnumerable<Transaction> Lines(DateOnly from, DateOnly to)
        {
            var accountIds = Request.AccountIds is { Count: > 0 } ids ? ids.ToHashSet() : null;
            return AllLines.Where(t => t.Date >= from && t.Date <= to
                && (accountIds is null || accountIds.Contains(t.AccountId))
                && (!t.Type.IsIncomeOrSpending() || CategoryIncluded(t.CategoryId)));
        }

        public PeriodTotals Totals(DateOnly from, DateOnly to) => LedgerMath.Totals(Lines(from, to), from, to);
    }
}
