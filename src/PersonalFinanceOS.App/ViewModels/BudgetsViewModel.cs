using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Parsing;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>One category of the month: spending, budget (editable), what is left and how it compares with history.</summary>
public sealed partial class BudgetRowViewModel : ViewModelBase
{
    private readonly Func<BudgetRowViewModel, Task> _save;

    public BudgetRowViewModel(CategoryAmount line, Func<BudgetRowViewModel, Task> save)
    {
        Line = line;
        _save = save;
        _editBudgetText = line.Budget is { } b ? b.ToEuros().ToString("0.00", Loc.Instance.Culture) : string.Empty;
    }

    public CategoryAmount Line { get; }

    public string Name => Line.Category?.Name ?? Loc.T("Budgets_Uncategorised");

    public bool IsArchived => Line.Category is { IsActive: false };

    /// <summary>Only active spending categories take a budget; lines without a category never do.</summary>
    public bool CanEditBudget => Line.Category is { IsActive: true, Kind: CategoryKind.Expense };

    public string ActualText => LedgerText.Amount(Line.Actual);

    public bool HasBudget => Line.Budget is not null;

    public bool IsOver => Line.IsOverBudget;

    /// <summary>For the bar; capped at 100 so an overspent budget shows a full bar (and the text says by how much).</summary>
    public double UsedPercent => Math.Clamp(Line.UsedPercent ?? 0, 0, 100);

    public string StatusText => Line.Remaining switch
    {
        null => Loc.T("Budgets_NoBudget"),
        { IsNegative: true } r => "⚠ " + Loc.Format("Budgets_Over", LedgerText.Amount(r.Abs())),
        { } r => Loc.Format("Budgets_Left", LedgerText.Amount(r)),
    };

    /// <summary>The usual amount, left out when there was none and there is none, so empty categories stay quiet.</summary>
    public string? HistoryText => Line.PreviousAverage is { } average && !(average.IsZero && Line.Actual.IsZero)
        ? Loc.Format("Budgets_Average", LedgerText.Amount(average)) + (Line.ChangePercent is { } p ? $" · {p:+0;-0;0} %" : string.Empty)
        : null;

    public bool IsUnusual => Line.IsUnusual;

    public bool IsLast { get; set; }

    [ObservableProperty]
    private string _editBudgetText;

    [ObservableProperty]
    private string? _error;

    [RelayCommand]
    private Task SaveBudget() => _save(this);
}

/// <summary>A planned occurrence of the month next to what happened.</summary>
public sealed record PlannedOutcomeViewModel(string Name, string PlannedText, string StateText, string? ActualText, string? DifferenceText, bool NeedsAttention)
{
    public bool IsLast { get; set; }
}

/// <summary>One month of the trend, with bars relative to the largest amount shown.</summary>
public sealed record TrendBarViewModel(string Label, bool HasData, string? Note, double Maximum, double Income, double Expenses, string IncomeText, string ExpensesText);

/// <summary>A plain line of label and amount.</summary>
public sealed record AmountLineViewModel(string Label, string AmountText, string? Detail = null);

/// <summary>
/// Budgets &amp; analysis (spec §13, §24 phase 5): one month at a time, spending per category against its budget and
/// the user's own history, income, recurring versus one-off, planned versus actual, shared accounts and a short trend.
/// Everything is calculated by <see cref="AnalysisService"/>; only setting a budget changes data.
/// </summary>
public sealed partial class BudgetsViewModel : PageViewModel
{
    private readonly AnalysisService _analysis;
    private readonly BudgetService _budgets;
    private readonly IClock _clock;
    private bool _active;
    private DateOnly _month;

    public BudgetsViewModel(AnalysisService analysis, BudgetService budgets, CategoryService categories, LedgerService ledger, RecurringService recurring, IClock clock)
    {
        _analysis = analysis;
        _budgets = budgets;
        _clock = clock;
        _month = MonthOf(clock.Today);
        void Refresh(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ReloadAsync();
            }
        });
        ledger.Changed += Refresh;
        recurring.Changed += Refresh;
        categories.Changed += Refresh;
        budgets.Changed += Refresh;
    }

    public DateOnly Month => _month;

    public string MonthTitle => _month.ToString("MMMM yyyy", Loc.Instance.Culture);

    [ObservableProperty]
    private bool _hasAccounts;

    [ObservableProperty]
    private bool _isCurrentMonth;

    /// <summary>Why the numbers of this month are not the full picture (still running, incomplete, no data).</summary>
    [ObservableProperty]
    private string? _monthNote;

    [ObservableProperty]
    private string _incomeText = string.Empty;

    [ObservableProperty]
    private string _expensesText = string.Empty;

    [ObservableProperty]
    private string _netText = string.Empty;

    [ObservableProperty]
    private bool _netIsNegative;

    [ObservableProperty]
    private string _comparisonText = string.Empty;

    [ObservableProperty]
    private string? _budgetTotalText;

    [ObservableProperty]
    private double _budgetUsedPercent;

    [ObservableProperty]
    private bool _budgetTotalIsOver;

    [ObservableProperty]
    private string? _unusualNote;

    [ObservableProperty]
    private string _recurringText = string.Empty;

    [ObservableProperty]
    private string _oneOffText = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<BudgetRowViewModel> Rows { get; } = [];

    public ObservableCollection<AmountLineViewModel> IncomeRows { get; } = [];

    public ObservableCollection<PlannedOutcomeViewModel> PlannedRows { get; } = [];

    public ObservableCollection<string> SharedLines { get; } = [];

    public ObservableCollection<TrendBarViewModel> Trend { get; } = [];

    public bool HasIncome => IncomeRows.Count > 0;

    public bool HasPlanned => PlannedRows.Count > 0;

    public bool HasShared => SharedLines.Count > 0;

    public override Task ActivateAsync()
    {
        _active = true;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task PreviousMonth() => GoToAsync(_month.AddMonths(-1));

    [RelayCommand]
    private Task NextMonth() => GoToAsync(_month.AddMonths(1));

    [RelayCommand]
    private Task ThisMonth() => GoToAsync(MonthOf(_clock.Today));

    public Task GoToAsync(DateOnly month)
    {
        _month = MonthOf(month);
        StatusMessage = null;
        OnPropertyChanged(nameof(Month));
        OnPropertyChanged(nameof(MonthTitle));
        return ReloadAsync();
    }

    private async Task SaveBudgetAsync(BudgetRowViewModel row)
    {
        row.Error = null;
        StatusMessage = null;
        var parsed = string.IsNullOrWhiteSpace(row.EditBudgetText) ? new AmountParseResult(true, Money.Zero, false) : AmountParser.Parse(row.EditBudgetText);
        if (!parsed.Success || parsed.Value.IsNegative || row.Line.Category is not { } category)
        {
            row.Error = Loc.Error("amount.invalid");
            return;
        }

        try
        {
            await _budgets.SetAsync(category.Id, _month, parsed.Value);
        }
        catch (DomainValidationException ex)
        {
            row.Error = Loc.Error(ex.Code);
            return;
        }

        StatusMessage = Loc.Format(parsed.Value.IsZero ? "Budgets_BudgetRemoved" : "Budgets_BudgetSaved", category.Name, MonthTitle);
    }

    private async Task ReloadAsync()
    {
        var today = _clock.Today;
        var a = await _analysis.AnalyzeMonthAsync(_month);
        HasAccounts = a.HasAccounts;
        IsCurrentMonth = a.IsCurrentMonth;
        MonthNote = a.HistoryStart switch
        {
            { } start when start > a.LastDay => Loc.Format("Budgets_NoData", LedgerText.Date(start, today)),
            { } start when start > a.Month => Loc.Format("Budgets_Incomplete", LedgerText.Date(start, today)),
            _ when a.IsCurrentMonth => Loc.Format("Budgets_InProgress", LedgerText.Date(today, today)),
            _ => null,
        };

        IncomeText = LedgerText.Amount(a.Totals.Income);
        ExpensesText = LedgerText.Amount(a.Totals.Expenses);
        NetText = LedgerText.Amount(a.Totals.Net);
        NetIsNegative = a.Totals.Net.IsNegative;
        ComparisonText = a.PreviousAverage switch
        {
            null => Loc.T("Budgets_NotCompared"),
            { } p when a.ComparedMonths == 1 => Loc.Format("Budgets_ComparedOne", LedgerText.Amount(p.Income), LedgerText.Amount(p.Expenses)),
            { } p => Loc.Format("Budgets_Compared", a.ComparedMonths, LedgerText.Amount(p.Income), LedgerText.Amount(p.Expenses)),
        };

        BudgetTotalText = a.BudgetTotal.IsPositive
            ? Loc.Format("Budgets_BudgetTotal", LedgerText.Amount(a.BudgetedSpending), LedgerText.Amount(a.BudgetTotal))
            : null;
        BudgetUsedPercent = a.BudgetTotal.IsPositive ? Math.Clamp(a.BudgetedSpending.Cents * 100.0 / a.BudgetTotal.Cents, 0, 100) : 0;
        BudgetTotalIsOver = a.BudgetedSpending > a.BudgetTotal && a.BudgetTotal.IsPositive;
        UnusualNote = a.CanShowUnusual ? null : Loc.Format("Budgets_UnusualNote", AnalysisService.UnusualMinimumMonths);

        Rows.Clear();
        foreach (var line in a.Expenses)
        {
            Rows.Add(new BudgetRowViewModel(line, SaveBudgetAsync));
        }

        if (Rows.Count > 0)
        {
            Rows[^1].IsLast = true;
        }

        IncomeRows.Clear();
        foreach (var line in a.Income)
        {
            IncomeRows.Add(new AmountLineViewModel(line.Category?.Name ?? Loc.T("Budgets_Uncategorised"), LedgerText.Amount(line.Actual)));
        }

        RecurringText = LedgerText.Amount(a.RecurringSpending);
        OneOffText = LedgerText.Amount(a.OneOffSpending);

        PlannedRows.Clear();
        foreach (var o in a.Planned)
        {
            PlannedRows.Add(Outcome(o, today));
        }

        if (PlannedRows.Count > 0)
        {
            PlannedRows[^1].IsLast = true;
        }

        SharedLines.Clear();
        foreach (var s in a.Shared)
        {
            SharedLines.Add(Loc.Format("Budgets_SharedLine", s.Account.Name, LedgerText.Amount(s.Spending), s.Share.Format(Loc.Instance.Culture), LedgerText.Amount(s.PersonalPart)));
        }

        Trend.Clear();
        var max = Math.Max(1, a.Trend.Where(p => p.HasData).Select(p => Math.Max(p.Totals.Income.Cents, p.Totals.Expenses.Cents)).DefaultIfEmpty(0).Max());
        foreach (var p in a.Trend)
        {
            var label = p.Month.ToString("MMM yyyy", Loc.Instance.Culture);
            var note = !p.HasData ? Loc.T("Budgets_TrendNoData") : p.IsPartial ? Loc.T("Budgets_TrendPartial") : null;
            Trend.Add(new TrendBarViewModel(
                label,
                p.HasData,
                note,
                max,
                p.HasData ? p.Totals.Income.Cents : 0,
                p.HasData ? p.Totals.Expenses.Cents : 0,
                Loc.Format("Budgets_TrendIn", LedgerText.Amount(p.Totals.Income)),
                Loc.Format("Budgets_TrendOut", LedgerText.Amount(p.Totals.Expenses))));
        }

        OnPropertyChanged(nameof(HasIncome));
        OnPropertyChanged(nameof(HasPlanned));
        OnPropertyChanged(nameof(HasShared));
        OnPropertyChanged(nameof(MonthTitle));
    }

    private static PlannedOutcomeViewModel Outcome(PlannedOutcome o, DateOnly today)
    {
        var overdue = o.State == OutcomeState.Open && o.DueDate < today;
        var state = overdue ? Loc.T("Outcome_Overdue") : Loc.T("Outcome_" + o.State);
        var planned = Loc.Format("Budgets_PlannedOn", LedgerText.Amount(o.Planned), LedgerText.Date(o.DueDate, today));
        var actual = o.Actual is { } t ? Loc.Format("Budgets_ActualOn", LedgerText.Amount(t.Amount.Abs()), LedgerText.Date(t.Date, today)) : null;
        var difference = o.Difference switch
        {
            { IsPositive: true } d => Loc.Format("Budgets_DifferenceMore", LedgerText.Amount(d)),
            { IsNegative: true } d => Loc.Format("Budgets_DifferenceLess", LedgerText.Amount(d.Abs())),
            _ => null,
        };
        return new PlannedOutcomeViewModel(o.Rule.Name, planned, state, actual, difference, overdue);
    }

    private static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MonthTitle));
        StatusMessage = null;
        if (_active)
        {
            _ = ReloadAsync();
        }
    }
}
