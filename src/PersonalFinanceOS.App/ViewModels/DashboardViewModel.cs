using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Analysis;
using PersonalFinanceOS.Application.Budgets;
using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Ledger;
using PersonalFinanceOS.Domain.Planning;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>Where an attention item leads.</summary>
public enum DashboardTarget
{
    Accounts,
    Planning,
    Budgets,
}

/// <summary>Something that needs the user (spec §13 "items needing attention"), with a way to go and look.</summary>
public sealed partial class AttentionItemViewModel(string text, DashboardTarget target, Action<DashboardTarget> open) : ViewModelBase
{
    public string Text { get; } = text;

    public DashboardTarget Target { get; } = target;

    [RelayCommand]
    private void Open() => open(Target);
}

/// <summary>A payment, transfer, income or card statement expected soon.</summary>
public sealed record UpcomingRowViewModel(string Name, string DateText, string AmountText, bool IsIncome)
{
    public bool IsLast { get; set; }
}

/// <summary>
/// The dashboard (spec §13): spendable money, safe-to-spend, what needs attention, this month against budget, what is
/// coming up, expected cash flow, credit cards, savings apart from spendable cash, and every account. Every number
/// comes from the same calculation services as the detailed pages.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    /// <summary>Days of upcoming items shown.</summary>
    public const int UpcomingDays = 14;

    private static readonly int[] CashFlowDays = [7, 30, 90];

    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly ForecastService _forecast;
    private readonly RecurringService _recurring;
    private readonly AnalysisService _analysis;
    private readonly CreditCardService _cards;
    private readonly ReconciliationService _reconciliation;
    private readonly IClock _clock;
    private bool _active;

    public DashboardViewModel(
        AccountService accounts,
        LedgerService ledger,
        ForecastService forecast,
        RecurringService recurring,
        AnalysisService analysis,
        CreditCardService cards,
        ReconciliationService reconciliation,
        BudgetService budgets,
        IClock clock)
    {
        _accounts = accounts;
        _ledger = ledger;
        _forecast = forecast;
        _recurring = recurring;
        _analysis = analysis;
        _cards = cards;
        _reconciliation = reconciliation;
        _clock = clock;
        void Refresh(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ActivateAsync();
            }
        });
        ledger.Changed += Refresh;
        recurring.Changed += Refresh;
        cards.Changed += Refresh;
        budgets.Changed += Refresh;
    }

    public Action? NavigateToAccounts { get; set; }

    public Action? NavigateToPlanning { get; set; }

    public Action? NavigateToBudgets { get; set; }

    public ObservableCollection<AttentionItemViewModel> Attention { get; } = [];

    public bool HasAttention => Attention.Count > 0;

    public ObservableCollection<UpcomingRowViewModel> Upcoming { get; } = [];

    public bool HasUpcoming => Upcoming.Count > 0;

    public ObservableCollection<CashFlowViewModel> CashFlows { get; } = [];

    public ObservableCollection<CardStatusViewModel> Cards { get; } = [];

    public bool HasCards => Cards.Count > 0;

    [ObservableProperty]
    private string? _budgetText;

    [ObservableProperty]
    private double _budgetUsedPercent;

    [ObservableProperty]
    private bool _budgetIsOver;

    [ObservableProperty]
    private string _savingsText = string.Empty;

    [ObservableProperty]
    private bool _hasSavings;

    [ObservableProperty]
    private bool _hasSafeToSpend;

    [ObservableProperty]
    private string _safeToSpendText = string.Empty;

    [ObservableProperty]
    private bool _safeIsNegative;

    [ObservableProperty]
    private string _safeCaption = string.Empty;

    [ObservableProperty]
    private string? _attentionText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountsSummary))]
    private int _activeAccountCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiquidTotalText))]
    private Money _liquidTotal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthIncomeText), nameof(MonthExpensesText), nameof(MonthNetText))]
    private PeriodTotals _monthTotals;

    public string AccountsSummary => Loc.Format("Dashboard_AccountsSummary", ActiveAccountCount);

    public string LiquidTotalText => LiquidTotal.Format(Loc.Instance.Culture);

    public string MonthTitle => _clock.Today.ToString("MMMM yyyy", Loc.Instance.Culture);

    public string MonthIncomeText => MonthTotals.Income.Format(Loc.Instance.Culture);

    public string MonthExpensesText => MonthTotals.Expenses.Format(Loc.Instance.Culture);

    public string MonthNetText => MonthTotals.Net.Format(Loc.Instance.Culture);

    public bool HasAccounts => ActiveAccountCount > 0;

    public System.Collections.ObjectModel.ObservableCollection<AccountRowViewModel> Tiles { get; } = [];

    public override async Task ActivateAsync()
    {
        _active = true;
        var list = await _accounts.ListAsync();
        var balances = await _ledger.GetBalancesAsync();
        var today = _clock.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        MonthTotals = await _ledger.GetTotalsAsync(monthStart, monthStart.AddMonths(1).AddDays(-1));
        ActiveAccountCount = list.Count;

        // Full balances of liquid accounts. Personal shares and net worth arrive in phase 6.
        LiquidTotal = Money.Sum(list.Where(a => a.Type.IsLiquid()).Select(a => balances[a.Id]));
        Tiles.Clear();
        foreach (var account in list)
        {
            Tiles.Add(new AccountRowViewModel(account, balances[account.Id], null, _ => Task.CompletedTask));
        }

        var safe = await _forecast.SafeToSpendAsync();
        HasSafeToSpend = safe.CanCalculate;
        SafeToSpendText = safe.Amount.Format(Loc.Instance.Culture);
        SafeIsNegative = safe.Amount.IsNegative;
        SafeCaption = Loc.Format("Dashboard_SafeUntil", LedgerText.Date(safe.Until, today));

        // Savings, pension and investments: not spendable, so never part of the figures above (spec §13).
        var saved = list.Where(a => a.Type is AccountType.Savings or AccountType.Pension or AccountType.Investment).ToList();
        HasSavings = saved.Count > 0;
        SavingsText = LedgerText.Amount(Money.Sum(saved.Select(a => balances[a.Id])));

        var open = await _recurring.ListOpenAsync(today.AddDays(UpcomingDays), withMatches: false);
        var due = open.Count(o => o.DueDate <= today);
        AttentionText = due == 0 ? null : Loc.Format(due == 1 ? "Dashboard_ToConfirmOne" : "Dashboard_ToConfirmMany", due);

        var month = await _analysis.AnalyzeMonthAsync(today);
        BudgetText = month.BudgetTotal.IsPositive
            ? Loc.Format("Budgets_BudgetTotal", LedgerText.Amount(month.BudgetedSpending), LedgerText.Amount(month.BudgetTotal))
            : null;
        BudgetUsedPercent = month.BudgetTotal.IsPositive ? Math.Clamp(month.BudgetedSpending.Cents * 100.0 / month.BudgetTotal.Cents, 0, 100) : 0;
        BudgetIsOver = month.BudgetTotal.IsPositive && month.BudgetedSpending > month.BudgetTotal;

        var statuses = await _cards.ListStatusesAsync();
        Cards.Clear();
        foreach (var status in statuses)
        {
            Cards.Add(new CardStatusViewModel(status, today));
        }

        LoadUpcoming(open, statuses, today, list);
        await LoadAttentionAsync(month, statuses, list);

        CashFlows.Clear();
        foreach (var f in await _forecast.CashFlowAsync(CashFlowDays))
        {
            CashFlows.Add(new CashFlowViewModel(Loc.Format("Planning_NextDays", f.Days), LedgerText.Amount(f.ExpectedIn), LedgerText.Amount(f.ExpectedOut), LedgerText.Amount(f.Net)));
        }

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(HasCards));
    }

    private void LoadUpcoming(IReadOnlyList<PlannedOccurrence> open, IReadOnlyList<CardStatus> statuses, DateOnly today, IReadOnlyList<Account> accounts)
    {
        var until = today.AddDays(UpcomingDays);
        var rows = new List<(DateOnly Date, UpcomingRowViewModel Row)>();
        foreach (var o in open.Where(o => o.DueDate >= today))
        {
            var income = o.Rule.Kind == RecurringKind.Income;
            rows.Add((o.DueDate, new UpcomingRowViewModel(
                o.Rule.Name,
                LedgerText.Date(o.DueDate, today),
                LedgerText.Amount(income ? o.Amount : o.Amount.Negate()),
                income)));
        }

        foreach (var s in statuses)
        {
            if (s.NextSettlement is { Date: { } date } settlement && date >= today && date <= until && settlement.Amount.IsPositive)
            {
                rows.Add((date, new UpcomingRowViewModel(
                    Loc.Format("Dashboard_CardSettlementItem", s.Card.Name),
                    LedgerText.Date(date, today),
                    LedgerText.Amount(settlement.Amount.Negate()),
                    false)));
            }
        }

        Upcoming.Clear();
        foreach (var (_, row) in rows.OrderBy(r => r.Date))
        {
            Upcoming.Add(row);
        }

        if (Upcoming.Count > 0)
        {
            Upcoming[^1].IsLast = true;
        }

        OnPropertyChanged(nameof(HasUpcoming));
    }

    private async Task LoadAttentionAsync(MonthAnalysis month, IReadOnlyList<CardStatus> statuses, IReadOnlyList<Account> accounts)
    {
        Attention.Clear();
        if (AttentionText is { } toConfirm)
        {
            Attention.Add(new AttentionItemViewModel(toConfirm, DashboardTarget.Planning, Open));
        }

        foreach (var over in month.OverBudget)
        {
            Attention.Add(new AttentionItemViewModel(
                Loc.Format("Dashboard_OverBudget", over.Category!.Name, LedgerText.Amount(over.Remaining!.Value.Abs())), DashboardTarget.Budgets, Open));
        }

        foreach (var s in statuses)
        {
            if (!s.IsSetUp)
            {
                Attention.Add(new AttentionItemViewModel(Loc.Format("Dashboard_CardNotSetUp", s.Card.Name), DashboardTarget.Accounts, Open));
            }
            else if (s.IsOverLimit)
            {
                Attention.Add(new AttentionItemViewModel(Loc.Format("Dashboard_CardOverLimit", s.Card.Name), DashboardTarget.Planning, Open));
            }

            if (s.SettlementDifference is not null)
            {
                Attention.Add(new AttentionItemViewModel(Loc.Format("Dashboard_CardDifference", s.Card.Name), DashboardTarget.Planning, Open));
            }
        }

        var latest = await _reconciliation.LatestAsync();
        foreach (var account in accounts)
        {
            if (latest.TryGetValue(account.Id, out var record) && !record.IsMatched)
            {
                Attention.Add(new AttentionItemViewModel(
                    Loc.Format("Dashboard_ReconcileDifference", account.Name, LedgerText.Amount(record.Difference)), DashboardTarget.Accounts, Open));
            }
        }

        OnPropertyChanged(nameof(HasAttention));
    }

    private void Open(DashboardTarget target)
    {
        var navigate = target switch
        {
            DashboardTarget.Accounts => NavigateToAccounts,
            DashboardTarget.Budgets => NavigateToBudgets,
            _ => NavigateToPlanning,
        };
        navigate?.Invoke();
    }

    [RelayCommand]
    private void GoToBudgets() => NavigateToBudgets?.Invoke();

    [RelayCommand]
    private void GoToAccounts() => NavigateToAccounts?.Invoke();

    [RelayCommand]
    private void GoToPlanning() => NavigateToPlanning?.Invoke();

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AccountsSummary));
        OnPropertyChanged(nameof(LiquidTotalText));
        OnPropertyChanged(nameof(MonthTitle));
        OnPropertyChanged(nameof(MonthIncomeText));
        OnPropertyChanged(nameof(MonthExpensesText));
        OnPropertyChanged(nameof(MonthNetText));
        if (_active)
        {
            _ = ActivateAsync();
        }
    }
}
