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
