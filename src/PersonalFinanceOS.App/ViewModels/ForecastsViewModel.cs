using CommunityToolkit.Mvvm.ComponentModel;
using PersonalFinanceOS.App.Localization;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record ForecastsTab(int Index, string Name);

/// <summary>The Planning page: planned items, cards and safe-to-spend, and the what-if scenarios next to them.</summary>
public sealed partial class ForecastsViewModel : PageViewModel
{
    public ForecastsViewModel(PlanningViewModel planning, ScenariosViewModel scenarios)
    {
        Planning = planning;
        Scenarios = scenarios;
        _tabs = Build();
        _selectedTab = _tabs[0];
    }

    public PlanningViewModel Planning { get; }

    public ScenariosViewModel Scenarios { get; }

    [ObservableProperty]
    private IReadOnlyList<ForecastsTab> _tabs;

    private int _index;

    [ObservableProperty]
    private ForecastsTab? _selectedTab;

    public PageViewModel Current => _index == 1 ? Scenarios : Planning;

    public override Task ActivateAsync() => Current.ActivateAsync();

    public void ShowPlanning() => SelectedTab = Tabs[0];

    public void ShowScenarios() => SelectedTab = Tabs[1];

    partial void OnSelectedTabChanged(ForecastsTab? value)
    {
        // Replacing the tabs after a language change briefly clears the selection; keep the page as it was.
        if (value is null || value.Index == _index)
        {
            return;
        }

        _index = value.Index;
        OnPropertyChanged(nameof(Current));
        _ = Current.ActivateAsync();
    }

    private static IReadOnlyList<ForecastsTab> Build() =>
        [new(0, Loc.T("Forecasts_TabPlanning")), new(1, Loc.T("Forecasts_TabScenarios"))];

    protected override void OnLanguageChanged()
    {
        Tabs = Build();
        SelectedTab = Tabs[_index];
    }
}
