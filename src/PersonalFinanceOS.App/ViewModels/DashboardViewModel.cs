using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Application.Ledger;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Ledger;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>
/// A starting point: balances and this month's income and spending. The full dashboard (safe-to-spend,
/// upcoming payments, VISA status…) arrives in phase 5 and uses the same calculation services.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly AccountService _accounts;
    private readonly LedgerService _ledger;
    private readonly IClock _clock;
    private bool _active;

    public DashboardViewModel(AccountService accounts, LedgerService ledger, IClock clock)
    {
        _accounts = accounts;
        _ledger = ledger;
        _clock = clock;
        ledger.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_active)
            {
                _ = ActivateAsync();
            }
        });
    }

    public Action? NavigateToAccounts { get; set; }

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

        OnPropertyChanged(nameof(HasAccounts));
    }

    [RelayCommand]
    private void GoToAccounts() => NavigateToAccounts?.Invoke();

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
