using CommunityToolkit.Mvvm.ComponentModel;
using PersonalFinanceOS.App.Localization;

namespace PersonalFinanceOS.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>A page in the main navigation.</summary>
public abstract class PageViewModel : ViewModelBase
{
    protected PageViewModel()
    {
        Loc.Instance.LanguageChanged += (_, _) => OnLanguageChanged();
    }

    /// <summary>Called each time the page is shown.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Re-raise computed, translated texts.</summary>
    protected virtual void OnLanguageChanged()
    {
    }
}

/// <summary>Sections that are scheduled for a later phase (spec §24).</summary>
public sealed class PlaceholderViewModel(string titleKey, int phase) : PageViewModel
{
    public string TitleKey { get; } = titleKey;
    public int Phase { get; } = phase;

    public string Title => Loc.T(TitleKey);
    public string Body => Loc.Format("Placeholder_Body", Phase);

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Body));
    }
}
