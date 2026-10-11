using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Assistant;

namespace PersonalFinanceOS.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string titleKey, string glyph, PageViewModel page)
    {
        TitleKey = titleKey;
        Glyph = glyph;
        Page = page;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Title));
    }

    public string TitleKey { get; }

    /// <summary>Path data for a small icon (drawn locally, no icon fonts or downloads).</summary>
    public string Glyph { get; }

    public Geometry Icon => _icon ??= StreamGeometry.Parse(Glyph);

    private Geometry? _icon;

    public PageViewModel Page { get; }

    public string Title => Loc.T(TitleKey);
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(
        DashboardViewModel dashboard,
        TransactionsViewModel transactions,
        AccountsViewModel accounts,
        ImportViewModel import,
        ForecastsViewModel forecasts,
        BudgetsViewModel budgets,
        WishlistViewModel wishlist,
        AssetsViewModel assets,
        AssistantViewModel assistant,
        SettingsViewModel settings,
        QuickAddViewModel quickAdd)
    {
        QuickAdd = quickAdd;
        NavItems =
        [
            new NavItem("Nav_Dashboard", Icons.Dashboard, dashboard),
            new NavItem("Nav_Transactions", Icons.Transactions, transactions),
            new NavItem("Nav_Accounts", Icons.Accounts, accounts),
            new NavItem("Nav_Import", Icons.Import, import),
            new NavItem("Nav_Budgets", Icons.Budgets, budgets),
            new NavItem("Nav_Forecasts", Icons.Forecasts, forecasts),
            new NavItem("Nav_Wishlist", Icons.Wishlist, wishlist),
            new NavItem("Nav_Assets", Icons.Assets, assets),
            new NavItem("Nav_Assistant", Icons.Assistant, assistant),
            new NavItem("Nav_Reports", Icons.Reports, new PlaceholderViewModel("Nav_Reports", 8)),
            new NavItem("Nav_Settings", Icons.Settings, settings),
        ];

        dashboard.NavigateToAccounts = () => SelectedItem = NavItems[2];
        dashboard.NavigateToPlanning = () =>
        {
            forecasts.ShowPlanning();
            SelectedItem = NavItems.First(n => n.Page == forecasts);
        };
        dashboard.NavigateToBudgets = () => SelectedItem = NavItems.First(n => n.Page == budgets);
        dashboard.NavigateToAssets = () => SelectedItem = NavItems.First(n => n.Page == assets);
        dashboard.NavigateToWishlist = () => SelectedItem = NavItems.First(n => n.Page == wishlist);
        assistant.Navigate = async (action, scenarioId) =>
        {
            switch (action)
            {
                case AssistantAction.OpenForecasts:
                    forecasts.ShowPlanning();
                    SelectedItem = NavItems.First(n => n.Page == forecasts);
                    break;
                case AssistantAction.OpenScenarios:
                    forecasts.ShowScenarios();
                    SelectedItem = NavItems.First(n => n.Page == forecasts);
                    if (scenarioId is { } id)
                    {
                        await forecasts.Scenarios.SelectAsync(id);
                    }

                    break;
                case AssistantAction.OpenWishlist:
                    SelectedItem = NavItems.First(n => n.Page == wishlist);
                    break;
                case AssistantAction.OpenBudgets:
                    SelectedItem = NavItems.First(n => n.Page == budgets);
                    break;
                case AssistantAction.OpenAccounts:
                    SelectedItem = NavItems[2];
                    break;
            }
        };
        _selectedItem = NavItems[0];
    }

    public ObservableCollection<NavItem> NavItems { get; }

    /// <summary>Quick Add is on every page (spec §7, §22).</summary>
    public QuickAddViewModel QuickAdd { get; }

    [ObservableProperty]
    private NavItem _selectedItem;

    public PageViewModel CurrentPage => SelectedItem.Page;

    partial void OnSelectedItemChanged(NavItem value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        _ = value.Page.ActivateAsync();
    }

    public Task InitializeAsync() => SelectedItem.Page.ActivateAsync();
}

/// <summary>Simple 24×24 outline icons as path data (original, drawn for this app).</summary>
internal static class Icons
{
    public const string Dashboard = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M3,14 H10 V21 H3 Z M14,14 H21 V21 H14 Z";
    public const string Transactions = "M4,7 H18 M14,3 L18,7 L14,11 M20,17 H6 M10,13 L6,17 L10,21";
    public const string Accounts = "M3,9 L12,4 L21,9 M5,9 V18 M9.5,9 V18 M14.5,9 V18 M19,9 V18 M3,20 H21";
    public const string Import = "M12,3 V14 M7.5,9.5 L12,14 L16.5,9.5 M4,15 V20 H20 V15";
    public const string Budgets = "M12,3 A9,9 0 1 0 21,12 H12 Z M15,3.5 A8,8 0 0 1 20.5,9 H15 Z";
    public const string Forecasts = "M3,20 H21 M4,16 L9,11 L13,14 L20,6 M16,6 H20 V10";
    public const string Wishlist = "M12,20 L4.5,12.5 A4.5,4.5 0 0 1 12,6.5 A4.5,4.5 0 0 1 19.5,12.5 Z";
    public const string Assets = "M4,20 V12 M9,20 V8 M14,20 V11 M19,20 V4 M3,20 H21";
    public const string Assistant = "M4,5 H20 V16 H10 L6,20 V16 H4 Z M8,10 H8.01 M12,10 H12.01 M16,10 H16.01";
    public const string Reports = "M6,3 H14 L19,8 V21 H6 Z M14,3 V8 H19 M9,13 H16 M9,17 H16";
    public const string Settings = "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 Z M12,2 V5 M12,19 V22 M2,12 H5 M19,12 H22 M4.9,4.9 L7,7 M17,17 L19.1,19.1 M4.9,19.1 L7,17 M17,7 L19.1,4.9";
}
