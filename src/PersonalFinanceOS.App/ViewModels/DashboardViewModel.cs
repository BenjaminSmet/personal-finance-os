using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Accounts;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;

namespace PersonalFinanceOS.App.ViewModels;

/// <summary>
/// Phase 1 dashboard: a starting point only. The real dashboard (safe-to-spend, upcoming payments,
/// VISA status…) arrives in phase 5 and will use the shared calculation services.
/// </summary>
public sealed partial class DashboardViewModel(AccountService accounts) : PageViewModel
{
    public Action? NavigateToAccounts { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountsSummary))]
    private int _activeAccountCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiquidOpeningTotalText))]
    private Money _liquidOpeningTotal;

    public string AccountsSummary => Loc.Format("Dashboard_AccountsSummary", ActiveAccountCount);

    public string LiquidOpeningTotalText => LiquidOpeningTotal.Format(Loc.Instance.Culture);

    public bool HasAccounts => ActiveAccountCount > 0;

    public System.Collections.ObjectModel.ObservableCollection<AccountRowViewModel> Tiles { get; } = [];

    public override async Task ActivateAsync()
    {
        var list = await accounts.ListAsync();
        ActiveAccountCount = list.Count;

        // Full balances of liquid accounts. Personal shares and net worth arrive in phase 6.
        LiquidOpeningTotal = Money.Sum(list.Where(a => a.Type.IsLiquid()).Select(a => a.OpeningBalance));
        Tiles.Clear();
        foreach (var account in list)
        {
            Tiles.Add(new AccountRowViewModel(account, _ => Task.CompletedTask));
        }

        OnPropertyChanged(nameof(HasAccounts));
    }

    [RelayCommand]
    private void GoToAccounts() => NavigateToAccounts?.Invoke();

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AccountsSummary));
        OnPropertyChanged(nameof(LiquidOpeningTotalText));
        _ = ActivateAsync();
    }
}
