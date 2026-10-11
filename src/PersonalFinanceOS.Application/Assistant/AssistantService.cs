using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Application.Scenarios;
using PersonalFinanceOS.Application.Wishlist;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Domain.Planning;
using PersonalFinanceOS.Domain.Scenarios;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Assistant;

/// <summary>
/// The local, rule-based financial assistant (spec §15). It recognises the question and answers with figures from the
/// calculation services only: it never does its own arithmetic on the user's words, never invents a figure, never treats
/// expected income as certain or card credit as money, and never changes data. When a missing value would change the
/// answer, it asks one focused question.
/// </summary>
public sealed class AssistantService(
    IAccountRepository accounts,
    ICategoryRepository categories,
    WishlistService wishlist,
    RecurringService recurring,
    ForecastService forecast,
    AffordabilityService affordability,
    AnalysisService analysis,
    ScenarioService scenarios,
    IClock clock)
{
    /// <summary>The days "What payments are coming up?" looks ahead.</summary>
    public const int UpcomingDays = 30;

    /// <summary>The most items listed under "taken into account"; the rest is counted.</summary>
    public const int MaxListed = 8;

    /// <summary>Example questions (string keys), shown as help and when a question is not understood.</summary>
    public static readonly string[] Examples =
    [
        "Ask_Example_Afford", "Ask_Example_Category", "Ask_Example_Safe", "Ask_Example_Upcoming", "Ask_Example_Lower",
        "Ask_Example_WhatIf", "Ask_Example_When", "Ask_Example_Spent", "Ask_Example_Changed", "Ask_Example_Card",
    ];

    /// <summary>Answers a question. <paramref name="pending"/> is the assistant's own last question, so a short reply can complete it.</summary>
    public async Task<AssistantAnswer> AskAsync(string text, PendingQuestion? pending = null, CancellationToken ct = default)
    {
        var today = clock.Today;
        var cats = await categories.ListAsync(includeArchived: false, ct);
        var open = (await wishlist.ListAsync(ct)).Where(e => e.IsOpen).Select(e => e.Item).ToList();
        var rules = await recurring.ListRulesAsync(includeEnded: false, ct);
        var q = QuestionParser.Parse(text, today, cats, open, rules);
        if (pending is not null && q.Intent == AssistantIntent.Unknown)
        {
            var p = pending.Question;
            text = pending.Text.Length > 0 ? pending.Text : text;
            q = p with
            {
                Amount = q.Amount ?? p.Amount,
                Category = q.Category ?? p.Category,
                Item = q.Item ?? p.Item,
                Period = q.Period ?? p.Period,
            };
        }

        var answer = q.Intent switch
        {
            AssistantIntent.CanAfford => await CanAffordAsync(q, open, ct),
            AssistantIntent.CategoryRoom => await CategoryRoomAsync(q, cats, ct),
            AssistantIntent.SafeToSpend => await SafeToSpendAsync(ct),
            AssistantIntent.Upcoming => await UpcomingAsync(ct),
            AssistantIntent.WhyLower => await WhyLowerAsync(ct),
            AssistantIntent.WhatIf => await WhatIfAsync(q, text, rules, open, ct),
            AssistantIntent.WhenAfford => await WhenAffordAsync(q, open, ct),
            AssistantIntent.SpentOn => await SpentOnAsync(q, cats, ct),
            AssistantIntent.ChangedVsLastMonth => await ChangedAsync(ct),
            AssistantIntent.CardOrWait => await CardOrWaitAsync(q, open, ct),
            AssistantIntent.Help => Help(AnswerKind.Direct, "Ask_Help"),
            _ => Help(AnswerKind.NotUnderstood, "Ask_NotUnderstood"),
        };
        return answer.Pending is { } asked ? answer with { Pending = asked with { Text = text } } : answer;
    }

    // ---- Can I afford this? -------------------------------------------------------------------------------------

    private async Task<AssistantAnswer> CanAffordAsync(ParsedQuestion q, IReadOnlyList<WishlistItem> open, CancellationToken ct)
    {
        if ((q.Amount ?? q.Item?.ExpectedPrice) is not { } price)
        {
            return Ask(q, MissingValue.Amount, "Ask_Q_Amount", open.Select(i => i.Name));
        }

        var a = await affordability.CheckAsync(price, q.Item, open, ct);
        if (!a.CanCalculate)
        {
            return NoSpendableAccount(q.Intent);
        }

        var what = What(q.Item, price);
        var days = a.LookAheadUntil.DayNumber - a.Today.DayNumber;
        var (kind, headline) = a.Verdict switch
        {
            AffordabilityVerdict.Prudent => (AnswerKind.Direct, T("Ask_Afford_Prudent", what, a.Buffer, days)),
            AffordabilityVerdict.TechnicallyOnly => (AnswerKind.Qualified, T("Ask_Afford_Technical", what, a.LowestAfter, a.LowestOn, a.Buffer)),
            _ => (AnswerKind.Direct, T("Ask_Afford_No", what, a.CashNow)),
        };

        var figures = AffordFigures(a);
        var considered = await ConsideredAsync(days, ct);
        var assumptions = await AssumptionsAsync(a.Caveats, a.NextIncome, ct);
        assumptions.Add(T("Ask_Assume_LookAhead", days));
        return new AssistantAnswer(
            q.Intent, kind, headline, figures, considered, assumptions,
            a.Verdict == AffordabilityVerdict.Prudent ? T("Ask_Explain_Prudent") : Earliest(a.EarliestPrudent, a.Today),
            [AssistantAction.OpenWishlist], []);
    }

    private static List<AnswerFigure> AffordFigures(Affordability a) =>
    [
        new(T("Check_CashNow"), a.CashNow),
        new(T("Check_CashAfter"), a.CashAfter),
        new(T("Check_Lowest", a.LookAheadUntil), T("Check_LowestOn", a.LowestAfter, a.LowestOn)),
        new(T("Check_Buffer", a.Buffer), T(a.BufferIntact ? "Check_Yes" : "Check_No")),
        new(T("Check_Bills"), T(a.BillsCovered ? "Check_Yes" : "Check_No")),
        new(T("Check_NextIncome"), a.NextIncome is { } income ? T("Check_Expected", income) : T("Check_NoIncome")),
        new(T("Check_Earliest"), a.EarliestPrudent is { } e ? e : T("Check_NoDate", AffordabilityService.ScanDays)),
    ];

    private static AnswerText Earliest(DateOnly? earliest, DateOnly today) => earliest switch
    {
        null => T("Ask_Earliest_None"),
        { } e when e == today => T("Ask_Explain_Prudent"),
        { } e => T("Ask_Earliest", e),
    };

    // ---- Can I spend another €100 on groceries this month? ------------------------------------------------------

    private async Task<AssistantAnswer> CategoryRoomAsync(ParsedQuestion q, IReadOnlyList<Category> cats, CancellationToken ct)
    {
        if (q.Amount is not { } amount)
        {
            return Ask(q, MissingValue.Amount, "Ask_Q_Amount", []);
        }

        if (q.Category is not { Kind: CategoryKind.Expense } category)
        {
            return Ask(q, MissingValue.Category, "Ask_Q_Category", cats.Where(c => c.Kind == CategoryKind.Expense).Select(c => c.Name));
        }

        var safe = await forecast.SafeToSpendAsync(ct);
        if (!safe.CanCalculate)
        {
            return NoSpendableAccount(q.Intent);
        }

        var month = await analysis.AnalyzeMonthAsync(clock.Today, ct);
        var line = month.Expenses.FirstOrDefault(e => e.Category?.Id == category.Id);
        var spent = line?.Actual ?? Money.Zero;
        var budget = line?.Budget;
        var fitsSafe = safe.Amount >= amount;
        AnswerKind kind;
        AnswerText headline;
        if (!fitsSafe)
        {
            (kind, headline) = (AnswerKind.Direct, T("Ask_Room_No", amount, Max0(safe.Amount), safe.Until));
        }
        else if (budget is not { } b)
        {
            (kind, headline) = (AnswerKind.Qualified, T("Ask_Room_NoBudget", amount, category.Name, safe.Amount, safe.Until));
        }
        else if (spent + amount <= b)
        {
            (kind, headline) = (AnswerKind.Direct, T("Ask_Room_Yes", amount, category.Name, b - spent, safe.Amount));
        }
        else
        {
            (kind, headline) = (AnswerKind.Qualified, T("Ask_Room_OverBudget", amount, category.Name, spent + amount - b));
        }

        var figures = new List<AnswerFigure>
        {
            new(T("Ask_F_SpentThisMonth", category.Name), spent),
            new(T("Ask_F_Budget"), budget is { } bb ? bb : T("Ask_V_NoBudget")),
        };
        if (line?.Remaining is { } left)
        {
            figures.Add(new(T("Ask_F_BudgetLeft"), left));
        }

        figures.Add(new(T("Ask_F_SafeUntil", safe.Until), safe.Amount));
        var assumptions = await AssumptionsAsync(safe.Caveats, safe.NextIncome?.DueDate, ct);
        return new AssistantAnswer(q.Intent, kind, headline, figures, [], assumptions, T("Ask_Explain_Room"), [AssistantAction.OpenBudgets], []);
    }

    // ---- How much can I safely spend before payday? ---------------------------------------------------------------

    private async Task<AssistantAnswer> SafeToSpendAsync(CancellationToken ct)
    {
        var s = await forecast.SafeToSpendAsync(ct);
        if (!s.CanCalculate)
        {
            return NoSpendableAccount(AssistantIntent.SafeToSpend);
        }

        var headline = s.Amount.IsNegative ? T("Ask_Safe_Short", s.Amount.Negate(), s.Until) : T("Ask_Safe", s.Amount, s.Until);
        var figures = new List<AnswerFigure>();
        foreach (var group in s.Lines.GroupBy(l => l.Kind))
        {
            var label = group.Key switch
            {
                ForecastLineKind.Balance => T("Ask_F_Balance"),
                ForecastLineKind.RecordedLater => T("Forecast_RecordedLater"),
                ForecastLineKind.Planned => T("Ask_F_PlannedOut"),
                ForecastLineKind.PlannedIn => T("Ask_F_PlannedIn"),
                ForecastLineKind.CardSettlement => T("Ask_F_CardStatements"),
                _ => T("Forecast_Buffer"),
            };
            figures.Add(new(label, Money.Sum(group.Select(l => l.Amount))));
        }

        figures.Add(new(T("Ask_F_SafeUntil", s.Until), s.Amount));
        var considered = s.Lines
            .Where(l => l.Kind is ForecastLineKind.Planned or ForecastLineKind.PlannedIn or ForecastLineKind.CardSettlement)
            .Select(l => l.Kind == ForecastLineKind.CardSettlement
                ? T("Ask_Event_Card", l.Name ?? string.Empty, l.Amount.Abs(), l.Date!.Value)
                : T(l.Amount.IsNegative ? "Ask_Event_Out" : "Ask_Event_In", l.Name ?? string.Empty, l.Amount.Abs(), l.Date!.Value))
            .ToList();
        var assumptions = await AssumptionsAsync(s.Caveats, s.NextIncome?.DueDate, ct);
        var explanation = s.NextIncome is not null ? T("Ask_Explain_Safe") : T("Ask_Explain_SafeNoIncome", ForecastService.DefaultHorizonDays);
        return new AssistantAnswer(
            AssistantIntent.SafeToSpend, s.Caveats.Count > 0 ? AnswerKind.Qualified : AnswerKind.Direct, headline, figures, Limit(considered), assumptions,
            explanation, [AssistantAction.OpenForecasts], []);
    }

    // ---- What payments are coming up? -----------------------------------------------------------------------------

    private async Task<AssistantAnswer> UpcomingAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var timeline = await forecast.TimelineAsync(today.AddDays(UpcomingDays), ct);
        if (timeline.Accounts.Count == 0)
        {
            return NoSpendableAccount(AssistantIntent.Upcoming);
        }

        var names = timeline.Accounts.ToDictionary(a => a.Id, a => a.Name);
        var outs = timeline.Events
            .Where(e => e.Amount.IsNegative && e.Kind is CashEventKind.Planned or CashEventKind.CardSettlement or CashEventKind.RecordedLater)
            .ToList();
        var total = Money.Sum(outs.Select(e => e.Amount)).Negate();
        var income = Money.Sum(timeline.Events.Where(e => e.Kind == CashEventKind.Income && timeline.Counted.Contains(e.AccountId)).Select(e => e.Amount));
        var headline = outs.Count == 0 ? T("Ask_Upcoming_None", UpcomingDays) : T("Ask_Upcoming", outs.Count, total, UpcomingDays);
        var considered = outs
            .Select(e => e.Kind == CashEventKind.CardSettlement
                ? T("Ask_Event_CardFrom", e.Name ?? string.Empty, e.Amount.Abs(), e.Date, names[e.AccountId])
                : T("Ask_Event_OutFrom", e.Name ?? string.Empty, e.Amount.Abs(), e.Date, names[e.AccountId]))
            .ToList();
        var figures = new List<AnswerFigure>
        {
            new(T("Ask_F_TotalOut", UpcomingDays), total),
            new(T("Ask_F_ExpectedIn", UpcomingDays), income),
        };
        var caveats = timeline.Caveats.Where(c => c != ForecastCaveat.NoIncomePlanned).ToList();
        var nextIncome = timeline.Events.FirstOrDefault(e => e.Kind == CashEventKind.Income && e.Date > today && timeline.Counted.Contains(e.AccountId));
        var assumptions = await AssumptionsAsync(caveats, nextIncome?.Date, ct, spendableOnly: false);
        return new AssistantAnswer(
            AssistantIntent.Upcoming, AnswerKind.Direct, headline, figures, Limit(considered, 12), assumptions,
            T("Ask_Explain_Upcoming"), [AssistantAction.OpenForecasts], []);
    }

    // ---- Why will my balance be lower next month? ---------------------------------------------------------------

    private async Task<AssistantAnswer> WhyLowerAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var nextMonth = thisMonth.AddMonths(1);
        var endThis = nextMonth.AddDays(-1);
        var endNext = nextMonth.AddMonths(1).AddDays(-1);
        var timeline = await forecast.TimelineAsync(endNext, ct);
        if (!timeline.CanCalculate)
        {
            return NoSpendableAccount(AssistantIntent.WhyLower);
        }

        var spendable = timeline.Spendable();
        var balanceThis = spendable.BalanceOn(endThis);
        var balanceNext = spendable.BalanceOn(endNext);
        var next = timeline.Events.Where(e => timeline.Counted.Contains(e.AccountId) && e.Date >= nextMonth && e.Date <= endNext).ToList();
        var moneyIn = Money.Sum(next.Where(e => e.Amount.IsPositive).Select(e => e.Amount));
        var moneyOut = Money.Sum(next.Where(e => e.Amount.IsNegative).Select(e => e.Amount)).Negate();
        var lower = balanceNext < balanceThis;
        var headline = lower
            ? T("Ask_Lower_Yes", balanceThis, balanceNext, moneyOut, moneyIn, new MonthRef(nextMonth))
            : T("Ask_Lower_No", balanceThis, balanceNext, new MonthRef(nextMonth));

        var rules = timeline.Open.Select(o => o.Rule).DistinctBy(r => r.Id).ToDictionary(r => r.Id);
        var considered = next.Where(e => e.Amount.IsNegative)
            .OrderBy(e => e.Amount.Cents)
            .Select(e =>
            {
                var rare = e.RuleId is { } id && rules.TryGetValue(id, out var r) && (r.Frequency is RecurrenceFrequency.Quarterly or RecurrenceFrequency.Yearly || r.EndDate == r.StartDate);
                return e.Kind == CashEventKind.CardSettlement
                    ? T("Ask_Event_Card", e.Name ?? string.Empty, e.Amount.Abs(), e.Date)
                    : T(rare ? "Ask_Event_Rare" : "Ask_Event_Out", e.Name ?? string.Empty, e.Amount.Abs(), e.Date);
            })
            .ToList();
        var figures = new List<AnswerFigure>
        {
            new(T("Ask_F_EndOfMonth", new MonthRef(thisMonth)), balanceThis),
            new(T("Ask_F_EndOfMonth", new MonthRef(nextMonth)), balanceNext),
            new(T("Ask_F_InMonth", new MonthRef(nextMonth)), moneyIn),
            new(T("Ask_F_OutMonth", new MonthRef(nextMonth)), moneyOut),
            new(T("Ask_F_LowestMonth", new MonthRef(nextMonth)), T("Check_LowestOn", spendable.Lowest(nextMonth, endNext).Balance, spendable.Lowest(nextMonth, endNext).Date)),
        };
        var incomeNext = next.FirstOrDefault(e => e.Kind == CashEventKind.Income);
        var assumptions = await AssumptionsAsync(timeline.Caveats.Where(c => c != ForecastCaveat.NoIncomePlanned).ToList(), incomeNext?.Date, ct);
        if (incomeNext is null)
        {
            assumptions.Insert(0, T("Ask_Assume_NoIncomeMonth", new MonthRef(nextMonth)));
        }

        return new AssistantAnswer(
            AssistantIntent.WhyLower, incomeNext is null ? AnswerKind.Qualified : AnswerKind.Direct, headline, figures, Limit(considered), assumptions,
            T("Ask_Explain_Lower"), [AssistantAction.OpenScenarios], []);
    }

    // ---- What if renovation costs increase by €5,000? -------------------------------------------------------------

    private async Task<AssistantAnswer> WhatIfAsync(ParsedQuestion q, string text, IReadOnlyList<RecurringRule> rules, IReadOnlyList<WishlistItem> open, CancellationToken ct)
    {
        if (q.Amount is not { } amount)
        {
            return Ask(q, MissingValue.Amount, "Ask_Q_WhatIfAmount", []);
        }

        var today = clock.Today;
        var main = (await accounts.ListAsync(includeArchived: false, ct)).Where(ForecastService.Counts).OrderBy(a => a.SortOrder).FirstOrDefault();
        if (main is null)
        {
            return NoSpendableAccount(q.Intent);
        }

        var name = Shorten(text);
        var described = new List<AnswerText>();
        ScenarioChangeRequest request;
        var rule = q.AboutIncome && q.Rule is not { Kind: RecurringKind.Income }
            ? rules.Where(r => r.Kind == RecurringKind.Income && r.IsActive).OrderByDescending(r => r.Amount.Cents).FirstOrDefault()
            : q.Rule;
        if (q.AboutIncome && rule is null)
        {
            return new AssistantAnswer(
                q.Intent, AnswerKind.Qualified, T("Ask_WhatIf_NoIncome"), [], [], [], null, [AssistantAction.OpenForecasts], []);
        }

        if (rule is not null)
        {
            var newAmount = q.IsDecrease ? Max0(rule.Amount - amount) : rule.Amount + amount;
            request = new ScenarioChangeRequest(ScenarioChangeKind.ChangePlanned, newAmount, today, RuleId: rule.Id);
            described.Add(T("Ask_Change_Planned", rule.Name, rule.Amount, newAmount));
        }
        else if (q.IsMonthly)
        {
            request = new ScenarioChangeRequest(ScenarioChangeKind.Monthly, amount, today, name, q.IsDecrease, AccountId: main.Id);
            described.Add(T(q.IsDecrease ? "Ask_Change_MonthlyIn" : "Ask_Change_MonthlyOut", amount, main.Name));
        }
        else
        {
            request = new ScenarioChangeRequest(ScenarioChangeKind.OneOff, amount, today, name, q.IsDecrease, AccountId: main.Id);
            described.Add(T(q.IsDecrease ? "Ask_Change_OneOffIn" : "Ask_Change_OneOffOut", amount, main.Name));
            described.Add(T("Ask_Assume_Today"));
        }

        var result = await scenarios.EvaluateAsync([request], ct);
        if (!result.CanCalculate)
        {
            return NoSpendableAccount(q.Intent);
        }

        var before = result.Baseline.Spendable();
        var after = result.Projected.Spendable();
        var lowBefore = before.Lowest(today, result.LastDay);
        var lowAfter = after.Lowest(today, result.LastDay);
        var newShort = result.ScenarioShortfalls
            .Where(s => s.Account is null && !result.BaselineShortfalls.Any(b => b.Account is null && b.Kind == s.Kind))
            .OrderBy(s => s.Kind == ShortfallKind.BelowZero ? 0 : 1)
            .FirstOrDefault();
        var (kind, headline) = newShort switch
        {
            { Kind: ShortfallKind.BelowZero } s => (AnswerKind.Qualified, T("Ask_WhatIf_BelowZero", s.Date, s.Balance)),
            { } s => (AnswerKind.Qualified, T("Ask_WhatIf_BelowBuffer", s.Date, s.Balance, before.Buffer)),
            _ => (AnswerKind.Direct, T("Ask_WhatIf_Ok", lowAfter.Balance, lowAfter.Date, lowBefore.Balance)),
        };

        var horizon = result.Horizons.Last();
        var figures = new List<AnswerFigure>
        {
            new(T("Ask_F_LowestYear"), T("Ask_V_FromTo", lowBefore.Balance, lowAfter.Balance)),
            new(T("Ask_F_InDays", horizon.Days), T("Ask_V_FromTo", horizon.Baseline, horizon.Scenario)),
            new(T("Ask_F_EndOfMonth", new MonthRef(new DateOnly(result.LastDay.Year, result.LastDay.Month, 1))), T("Ask_V_FromTo", before.BalanceOn(result.LastDay), after.BalanceOn(result.LastDay))),
        };
        foreach (var other in result.ScenarioShortfalls.Where(s => s.Account is not null && !result.BaselineShortfalls.Any(b => b.Account?.Id == s.Account.Id)))
        {
            figures.Add(new(T("Ask_F_AccountBelowZero", other.Account!.Name), T("Check_LowestOn", other.Balance, other.Date)));
        }

        var considered = result.Wishlist.Where(w => w.IsDelayed)
            .Select(w => T("Effect_Delayed", w.Item.Name, w.Before is { } b ? b : T("Effect_NoDate"), w.After is { } a ? a : T("Effect_NoDate")))
            .ToList();
        if (considered.Count == 0 && result.Wishlist.Count > 0)
        {
            considered.Add(T("Effect_None"));
        }

        var assumptions = described;
        assumptions.AddRange(result.Changes.Where(c => c.Note != ScenarioNote.None).Select(c => T("ScenarioNote_" + c.Note)));
        assumptions.AddRange(await AssumptionsAsync(result.Baseline.Caveats, result.Baseline.Spendable().NextIncomeAfter(today), ct));
        return new AssistantAnswer(
            q.Intent, kind, headline, figures, considered, assumptions, T("Ask_Explain_WhatIf"),
            [AssistantAction.SaveScenario, AssistantAction.OpenScenarios], [],
            Scenario: [request], ScenarioName: name);
    }

    // ---- When can I afford a wishlist item? ----------------------------------------------------------------------

    private async Task<AssistantAnswer> WhenAffordAsync(ParsedQuestion q, IReadOnlyList<WishlistItem> open, CancellationToken ct)
    {
        if ((q.Item?.ExpectedPrice ?? q.Amount) is not { } price)
        {
            return open.Count == 0
                ? Ask(q, MissingValue.Amount, "Ask_Q_Amount", [])
                : Ask(q, MissingValue.Item, "Ask_Q_Item", open.Select(i => i.Name));
        }

        var a = await affordability.CheckAsync(price, q.Item, open, ct);
        if (!a.CanCalculate)
        {
            return NoSpendableAccount(q.Intent);
        }

        var what = What(q.Item, price);
        var headline = a.EarliestPrudent switch
        {
            null => T("Ask_When_Never", what),
            { } e when e == a.Today => T("Ask_When_Now", what),
            { } e => T("Ask_When_Date", what, e),
        };
        var considered = new List<AnswerText>();
        if (q.Item?.TargetDate is { } target)
        {
            considered.Add(T(a.EarliestPrudent is { } e && e <= target ? "Ask_When_BeforeTarget" : "Ask_When_AfterTarget", target));
        }

        if (q.Item?.MonthlySaving is { } saving)
        {
            considered.Add(T("Ask_When_Saving", saving));
        }

        considered.AddRange(a.Effects.Where(e => e.IsDelayed)
            .Select(e => T("Effect_Delayed", e.Item.Name, e.Before is { } b ? b : T("Effect_NoDate"), e.After is { } af ? af : T("Effect_NoDate"))));
        var assumptions = await AssumptionsAsync(a.Caveats, a.NextIncome, ct);
        assumptions.Add(T("Ask_Assume_NothingElse"));
        return new AssistantAnswer(
            q.Intent, a.EarliestPrudent is null ? AnswerKind.Qualified : AnswerKind.Direct, headline, AffordFigures(a), considered, assumptions,
            T("Ask_Explain_When", a.LookAheadUntil.DayNumber - a.Today.DayNumber), [AssistantAction.OpenWishlist], []);
    }

    // ---- How much did I spend on subscriptions? -------------------------------------------------------------------

    private async Task<AssistantAnswer> SpentOnAsync(ParsedQuestion q, IReadOnlyList<Category> cats, CancellationToken ct)
    {
        if (q.Category is not { } category)
        {
            return Ask(q, MissingValue.Category, "Ask_Q_Category", cats.Select(c => c.Name));
        }

        var today = clock.Today;
        var period = q.Period ?? new QuestionPeriod(PeriodKind.ThisMonth, new DateOnly(today.Year, today.Month, 1), today);
        var months = new List<(DateOnly Month, Money Amount, Money? Budget)>();
        DateOnly? historyStart = null;
        var incomplete = false;
        for (var m = new DateOnly(period.From.Year, period.From.Month, 1); m <= period.To; m = m.AddMonths(1))
        {
            var month = await analysis.AnalyzeMonthAsync(m, ct);
            historyStart ??= month.HistoryStart;
            incomplete |= month.IsIncomplete;
            var lines = category.Kind == CategoryKind.Income ? month.Income : month.Expenses;
            var line = lines.FirstOrDefault(l => l.Category?.Id == category.Id);
            months.Add((m, line?.Actual ?? Money.Zero, line?.Budget));
        }

        if (historyStart is null)
        {
            return NoSpendableAccount(q.Intent);
        }

        var total = Money.Sum(months.Select(m => m.Amount));
        var periodText = Period(period);
        var headline = T(category.Kind == CategoryKind.Income ? "Ask_Received" : "Ask_Spent", total, category.Name, periodText);
        var figures = new List<AnswerFigure>();
        if (months.Count > 1)
        {
            figures.AddRange(months.Select(m => new AnswerFigure(T("Ask_F_Month", new MonthRef(m.Month)), m.Amount)));
            figures.Add(new(T("Ask_F_Average"), Money.FromCents((long)Math.Round(total.Cents / (decimal)months.Count, MidpointRounding.AwayFromZero))));
        }
        else if (months[0].Budget is { } budget)
        {
            figures.Add(new(T("Ask_F_Budget"), budget));
            figures.Add(new(T("Ask_F_BudgetLeft"), budget - months[0].Amount));
        }

        var assumptions = new List<AnswerText>();
        if (incomplete)
        {
            assumptions.Add(T("Ask_Assume_History", historyStart.Value));
        }

        if (period.To >= today && period.From <= today)
        {
            assumptions.Add(T("Ask_Assume_SoFar", today));
        }

        assumptions.Add(T("Ask_Assume_Refunds"));
        return new AssistantAnswer(q.Intent, incomplete ? AnswerKind.Qualified : AnswerKind.Direct, headline, figures, [], assumptions, null, [AssistantAction.OpenBudgets], []);
    }

    private static AnswerText Period(QuestionPeriod p) => p.Kind switch
    {
        PeriodKind.LastMonth => T("Ask_Period_Month", new MonthRef(p.From)),
        PeriodKind.ThisYear => T("Ask_Period_ThisYear", p.From.Year),
        PeriodKind.LastMonths => T("Ask_Period_LastMonths", p.Months),
        PeriodKind.Month => T("Ask_Period_Month", new MonthRef(p.From)),
        _ => T("Ask_Period_ThisMonth"),
    };

    // ---- What changed versus last month? -------------------------------------------------------------------------

    private async Task<AssistantAnswer> ChangedAsync(CancellationToken ct)
    {
        var today = clock.Today;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var last = await analysis.AnalyzeMonthAsync(thisMonth.AddMonths(-1), ct);
        var before = await analysis.AnalyzeMonthAsync(thisMonth.AddMonths(-2), ct);
        if (!last.HasAccounts)
        {
            return NoSpendableAccount(AssistantIntent.ChangedVsLastMonth);
        }

        var lastRef = new MonthRef(last.Month);
        var beforeRef = new MonthRef(before.Month);
        if (last.IsIncomplete || before.IsIncomplete)
        {
            return new AssistantAnswer(
                AssistantIntent.ChangedVsLastMonth, AnswerKind.Qualified, T("Ask_Changed_NoHistory", lastRef, beforeRef, last.HistoryStart!.Value),
                [], [], [], null, [AssistantAction.OpenBudgets], []);
        }

        var diff = last.Totals.Expenses - before.Totals.Expenses;
        var headline = diff.IsPositive ? T("Ask_Changed_More", lastRef, diff, beforeRef)
            : diff.IsNegative ? T("Ask_Changed_Less", lastRef, diff.Negate(), beforeRef)
            : T("Ask_Changed_Same", lastRef, beforeRef);
        var figures = new List<AnswerFigure>
        {
            new(T("Ask_F_Income"), T("Ask_V_FromTo", before.Totals.Income, last.Totals.Income)),
            new(T("Ask_F_Spending"), T("Ask_V_FromTo", before.Totals.Expenses, last.Totals.Expenses)),
            new(T("Ask_F_Net"), T("Ask_V_FromTo", before.Totals.Income - before.Totals.Expenses, last.Totals.Income - last.Totals.Expenses)),
        };

        var ids = last.Expenses.Concat(before.Expenses).Select(e => e.Category?.Id).Distinct().ToList();
        var changes = ids.Select(id =>
            {
                var a = last.Expenses.FirstOrDefault(e => e.Category?.Id == id);
                var b = before.Expenses.FirstOrDefault(e => e.Category?.Id == id);
                var name = (a ?? b)!.Category?.Name;
                return (Name: name, Before: b?.Actual ?? Money.Zero, After: a?.Actual ?? Money.Zero);
            })
            .Where(c => c.After != c.Before)
            .ToList();
        var considered = changes.Where(c => c.After > c.Before).OrderByDescending(c => (c.After - c.Before).Cents).Take(3)
            .Select(c => c.Name is null ? T("Ask_Cat_UpUncategorised", c.After - c.Before, c.Before, c.After) : T("Ask_Cat_Up", c.Name, c.After - c.Before, c.Before, c.After))
            .Concat(changes.Where(c => c.After < c.Before).OrderByDescending(c => (c.Before - c.After).Cents).Take(3)
                .Select(c => c.Name is null ? T("Ask_Cat_DownUncategorised", c.Before - c.After, c.Before, c.After) : T("Ask_Cat_Down", c.Name, c.Before - c.After, c.Before, c.After)))
            .ToList();
        return new AssistantAnswer(
            AssistantIntent.ChangedVsLastMonth, AnswerKind.Direct, headline, figures, considered,
            [T("Ask_Assume_CompleteMonths", new MonthRef(thisMonth)), T("Ask_Assume_Refunds")], null, [AssistantAction.OpenBudgets], []);
    }

    // ---- Should I use VISA or wait? ------------------------------------------------------------------------------

    private async Task<AssistantAnswer> CardOrWaitAsync(ParsedQuestion q, IReadOnlyList<WishlistItem> open, CancellationToken ct)
    {
        if ((q.Amount ?? q.Item?.ExpectedPrice) is not { } price)
        {
            return Ask(q, MissingValue.Amount, "Ask_Q_Amount", open.Select(i => i.Name));
        }

        var a = await affordability.CheckAsync(price, q.Item, open, ct);
        if (!a.CanCalculate)
        {
            return NoSpendableAccount(q.Intent);
        }

        var what = What(q.Item, price);
        var now = a.Options.First(o => o.Method == PaymentMethod.CurrentAccount);
        var cardOptions = a.Options.Where(o => o.Method == PaymentMethod.CreditCard).ToList();
        var goodCard = cardOptions.FirstOrDefault(o => o.Verdict == AffordabilityVerdict.Prudent);
        var (kind, headline) = (now.Verdict, goodCard, a.EarliestPrudent) switch
        {
            (AffordabilityVerdict.Prudent, _, _) => (AnswerKind.Direct, T("Ask_Card_PayNow", what)),
            (_, { } card, _) => (AnswerKind.Direct, T("Ask_Card_UseCard", what, card.Card!.Name, card.PaidOn!.Value, card.StatementAfter!.Value)),
            (_, null, { } date) => (AnswerKind.Direct, T(cardOptions.Count == 0 ? "Ask_Card_WaitNoCard" : "Ask_Card_Wait", what, date)),
            _ => (AnswerKind.Qualified, T("Ask_Card_None", what)),
        };

        var figures = new List<AnswerFigure>
        {
            new(T("Pay_Now"), T("Ask_V_Option", T("Verdict_" + now.Verdict), T("Pay_NowDetail", now.LowestBalance ?? Money.Zero))),
        };
        foreach (var c in cardOptions)
        {
            var detail = c.Note switch
            {
                PaymentNote.OverLimit => T("Pay_CardOverLimit", c.AvailableCreditAfter!.Value.Negate()),
                PaymentNote.SettlementAccountUnknown => T("Pay_CardNoAccount"),
                PaymentNote.SettlementAccountNotCounted => T("Pay_CardOtherAccount", c.PaidOn!.Value),
                PaymentNote.CardNotSetUp => T("ForecastCaveat_CardNotSetUp"),
                _ => T("Pay_CardDetail", c.PaidOn!.Value, c.StatementAfter!.Value, c.AvailableCreditAfter!.Value, c.LowestBalance!.Value),
            };
            figures.Add(new(T("Pay_Card", c.Card!.Name), T("Ask_V_Option", T("Verdict_" + c.Verdict), detail)));
        }

        figures.Add(new(T("Pay_Postpone"), a.EarliestPrudent is { } wait ? T("Pay_PostponeUntil", wait) : T("Effect_NoDate")));
        var assumptions = await AssumptionsAsync(a.Caveats, a.NextIncome, ct);
        assumptions.Insert(0, T("Ask_Assume_Credit"));
        return new AssistantAnswer(q.Intent, kind, headline, figures, await ConsideredAsync(a.LookAheadUntil.DayNumber - a.Today.DayNumber, ct), assumptions,
            T("Ask_Explain_Card"), [AssistantAction.OpenWishlist], []);
    }

    // ---- Shared pieces -------------------------------------------------------------------------------------------

    /// <summary>The expected bills, card statements and income the spendable forecast counts in the coming days.</summary>
    private async Task<List<AnswerText>> ConsideredAsync(int days, CancellationToken ct)
    {
        var projection = await forecast.ProjectAsync(days, ct);
        var list = projection.Events
            .Where(e => e.Kind is CashEventKind.Planned or CashEventKind.PlannedIn or CashEventKind.CardSettlement or CashEventKind.Income or CashEventKind.RecordedLater)
            .Select(e => e.Kind switch
            {
                CashEventKind.CardSettlement => T("Ask_Event_Card", e.Name ?? string.Empty, e.Amount.Abs(), e.Date),
                CashEventKind.Income => T("Ask_Event_Income", e.Name ?? string.Empty, e.Amount, e.Date),
                _ => T(e.Amount.IsNegative ? "Ask_Event_Out" : "Ask_Event_In", e.Name ?? string.Empty, e.Amount.Abs(), e.Date),
            })
            .ToList();
        return Limit(list);
    }

    /// <summary>
    /// What every forecast answer assumes: expected income is expected, not certain; money on savings or the joint account
    /// is not spendable unless a transfer is planned; plus the forecast's own caveats.
    /// </summary>
    private async Task<List<AnswerText>> AssumptionsAsync(IReadOnlyList<ForecastCaveat> caveats, DateOnly? nextIncome, CancellationToken ct, bool spendableOnly = true)
    {
        var list = new List<AnswerText>();
        if (nextIncome is { } income)
        {
            list.Add(T("Ask_Assume_Income", income));
        }

        if (spendableOnly && (await accounts.ListAsync(includeArchived: false, ct)).Any(a => ForecastService.Projects(a) && !ForecastService.Counts(a)))
        {
            list.Add(T("Ask_Assume_NotSpendable"));
        }

        list.AddRange(caveats.Select(c => T("ForecastCaveat_" + c)));
        return list;
    }

    private static AssistantAnswer Ask(ParsedQuestion q, MissingValue missing, string key, IEnumerable<string> suggestions) =>
        new(q.Intent, AnswerKind.Question, T(key), [], [], [], null, [], suggestions.Take(8).ToList(), new PendingQuestion(q, missing));

    private static AssistantAnswer NoSpendableAccount(AssistantIntent intent) =>
        new(intent, AnswerKind.Qualified, T("Ask_NoAccount"), [], [], [], T("Verdict_CannotCalculateWhy"), [AssistantAction.OpenAccounts], []);

    private static AssistantAnswer Help(AnswerKind kind, string key) =>
        new(AssistantIntent.Help, kind, T(key), [], [], [], null, [], Examples, SuggestionsAreExamples: true);

    private static AnswerText What(WishlistItem? item, Money price) => item is null ? T("Ask_What_Amount", price) : T("Ask_What_Item", item.Name, price);

    private static List<AnswerText> Limit(List<AnswerText> list, int max = MaxListed)
    {
        if (list.Count <= max)
        {
            return list;
        }

        var shown = list.Take(max).ToList();
        shown.Add(T("Ask_More", list.Count - max));
        return shown;
    }

    private static string Shorten(string text)
    {
        var trimmed = text.Trim().TrimEnd('?', '!', '.').Trim();
        return trimmed.Length <= ScenarioChange.MaxNameLength ? trimmed : trimmed[..ScenarioChange.MaxNameLength].TrimEnd();
    }

    private static Money Max0(Money m) => m.IsNegative ? Money.Zero : m;

    private static AnswerText T(string key, params object[] args) => new(key, args);
}
