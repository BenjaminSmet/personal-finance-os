using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;
using PersonalFinanceOS.Infrastructure;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record LanguageOption(AppLanguage Language, string Name);

public sealed record CategoryKindOption(CategoryKind Kind, string Name);

public enum CategoryAction
{
    Rename,
    Archive,
    MoveUp,
    MoveDown,
    Merge,
    Reactivate,
}

public sealed partial class CategoryRowViewModel(Category category, Func<CategoryRowViewModel, CategoryAction, Task> act) : ViewModelBase
{
    public Category Category { get; } = category;

    public string Name => Category.Name;

    public string KindName => Loc.T("CategoryKind_" + Category.Kind);

    public bool IsLast { get; set; }

    /// <summary>Not the first of its kind, so it can move up.</summary>
    public bool CanMoveUp { get; set; }

    /// <summary>Not the last of its kind, so it can move down.</summary>
    public bool CanMoveDown { get; set; }

    [RelayCommand]
    private Task Rename() => act(this, CategoryAction.Rename);

    [RelayCommand]
    private Task Archive() => act(this, CategoryAction.Archive);

    [RelayCommand]
    private Task MoveUp() => act(this, CategoryAction.MoveUp);

    [RelayCommand]
    private Task MoveDown() => act(this, CategoryAction.MoveDown);

    [RelayCommand]
    private Task Merge() => act(this, CategoryAction.Merge);

    [RelayCommand]
    private Task Reactivate() => act(this, CategoryAction.Reactivate);
}

public sealed partial class ThemeOption : ObservableObject
{
    public ThemeOption(AppTheme theme)
    {
        Theme = theme;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Name));
    }

    public AppTheme Theme { get; }

    public string Name => Loc.T("Theme_" + Theme);
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppSettingsService _settings;
    private readonly CategoryService _categories;
    private bool _loading;
    private CategoryRowViewModel? _renaming;

    public SettingsViewModel(AppSettingsService settings, CategoryService categories, AppDataPaths paths, AppRuntimeInfo runtime)
    {
        _settings = settings;
        _categories = categories;
        CategoryKinds = CategoryKindOptions();
        _newCategoryKind = CategoryKinds[0];
        DataFolder = paths.Root;
        AppVersion = runtime.AppVersion;
        SchemaVersion = runtime.SchemaVersion;
        Languages =
        [
            new LanguageOption(AppLanguage.Dutch, "Nederlands"),
            new LanguageOption(AppLanguage.English, "English"),
        ];
        Themes = [new ThemeOption(AppTheme.System), new ThemeOption(AppTheme.Light), new ThemeOption(AppTheme.Dark)];
        _selectedLanguage = Languages.First(l => l.Language == Loc.Instance.Language);
        _selectedTheme = Themes.First(t => t.Theme == ThemeManager.Current);
    }

    public IReadOnlyList<LanguageOption> Languages { get; }
    public IReadOnlyList<ThemeOption> Themes { get; }

    public string DataFolder { get; }
    public string AppVersion { get; }
    public int SchemaVersion { get; }

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    // ===== Categories (spec §13: create, rename, merge, reorder, archive) =====

    public System.Collections.ObjectModel.ObservableCollection<CategoryRowViewModel> CategoryRows { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<CategoryRowViewModel> ArchivedCategoryRows { get; } = [];

    public bool HasArchivedCategories => ArchivedCategoryRows.Count > 0;

    [ObservableProperty]
    private string? _categoryMessage;

    /// <summary>The category being merged; the merge panel is open while set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMerging), nameof(MergeTitle), nameof(MergeExplain))]
    private CategoryRowViewModel? _mergeSource;

    [ObservableProperty]
    private IReadOnlyList<CategoryOption> _mergeTargets = [];

    [ObservableProperty]
    private CategoryOption? _mergeTarget;

    public bool IsMerging => MergeSource is not null;

    public string MergeTitle => MergeSource is { } s ? Loc.Format("Settings_MergeInto", s.Name) : string.Empty;

    public string MergeExplain => MergeSource is { } s ? Loc.Format("Settings_MergeExplain", s.Name) : string.Empty;

    [ObservableProperty]
    private IReadOnlyList<CategoryKindOption> _categoryKinds;

    [ObservableProperty]
    private string _newCategoryName = string.Empty;

    [ObservableProperty]
    private CategoryKindOption _newCategoryKind;

    [ObservableProperty]
    private string? _categoryError;

    [ObservableProperty]
    private bool _isRenaming;

    public override async Task ActivateAsync()
    {
        _loading = true;
        SelectedLanguage = Languages.First(l => l.Language == Loc.Instance.Language);
        SelectedTheme = Themes.First(t => t.Theme == ThemeManager.Current);
        _loading = false;
        await ReloadCategoriesAsync();
    }

    [RelayCommand]
    private async Task SaveCategoryAsync()
    {
        CategoryError = null;
        CategoryMessage = null;
        try
        {
            if (_renaming is { } row)
            {
                await _categories.RenameAsync(row.Category.Id, NewCategoryName);
            }
            else
            {
                await _categories.CreateAsync(NewCategoryName, NewCategoryKind.Kind);
            }
        }
        catch (DomainValidationException ex)
        {
            CategoryError = Loc.Error(ex.Code);
            return;
        }

        CancelCategoryEdit();
        await ReloadCategoriesAsync();
    }

    [RelayCommand]
    private void CancelCategoryEdit()
    {
        _renaming = null;
        IsRenaming = false;
        NewCategoryName = string.Empty;
        CategoryError = null;
    }

    private async Task ActOnCategoryAsync(CategoryRowViewModel row, CategoryAction action)
    {
        CategoryError = null;
        CategoryMessage = null;
        try
        {
            switch (action)
            {
                case CategoryAction.Rename:
                    _renaming = row;
                    IsRenaming = true;
                    NewCategoryName = row.Name;
                    NewCategoryKind = CategoryKinds.First(k => k.Kind == row.Category.Kind);
                    return;
                case CategoryAction.Merge:
                    MergeTargets = [.. CategoryRows.Where(r => r.Category.Kind == row.Category.Kind && r != row).Select(r => new CategoryOption(r.Category.Id, r.Name))];
                    MergeTarget = MergeTargets.FirstOrDefault();
                    MergeSource = row;
                    return;
                case CategoryAction.Archive:
                    await _categories.ArchiveAsync(row.Category.Id);
                    break;
                case CategoryAction.Reactivate:
                    await _categories.ReactivateAsync(row.Category.Id);
                    break;
                case CategoryAction.MoveUp:
                    await _categories.MoveAsync(row.Category.Id, -1);
                    break;
                case CategoryAction.MoveDown:
                    await _categories.MoveAsync(row.Category.Id, +1);
                    break;
            }
        }
        catch (DomainValidationException ex)
        {
            CategoryError = Loc.Error(ex.Code);
        }

        await ReloadCategoriesAsync();
    }

    [RelayCommand]
    private async Task ConfirmMergeAsync()
    {
        if (MergeSource is not { } source || MergeTarget?.Id is not { } targetId)
        {
            CategoryError = Loc.Error("category.merge.invalid");
            return;
        }

        try
        {
            await _categories.MergeAsync(source.Category.Id, targetId);
        }
        catch (DomainValidationException ex)
        {
            CategoryError = Loc.Error(ex.Code);
            return;
        }

        CategoryMessage = Loc.Format("Settings_Merged", source.Name, MergeTarget.Name);
        MergeSource = null;
        await ReloadCategoriesAsync();
    }

    [RelayCommand]
    private void CancelMerge() => MergeSource = null;

    private async Task ReloadCategoriesAsync()
    {
        var all = await _categories.ListAsync(includeArchived: true);
        var list = all.Where(c => c.IsActive).ToList();
        CategoryRows.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            CategoryRows.Add(new CategoryRowViewModel(list[i], ActOnCategoryAsync)
            {
                IsLast = i == list.Count - 1,
                CanMoveUp = i > 0 && list[i - 1].Kind == list[i].Kind,
                CanMoveDown = i < list.Count - 1 && list[i + 1].Kind == list[i].Kind,
            });
        }

        var archived = all.Where(c => !c.IsActive).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ArchivedCategoryRows.Clear();
        for (var i = 0; i < archived.Count; i++)
        {
            ArchivedCategoryRows.Add(new CategoryRowViewModel(archived[i], ActOnCategoryAsync) { IsLast = i == archived.Count - 1 });
        }

        OnPropertyChanged(nameof(HasArchivedCategories));
    }

    private static List<CategoryKindOption> CategoryKindOptions() =>
        [.. Enum.GetValues<CategoryKind>().Select(k => new CategoryKindOption(k, Loc.T("CategoryKind_" + k)))];

    protected override void OnLanguageChanged()
    {
        var kind = NewCategoryKind.Kind;
        CategoryKinds = CategoryKindOptions();
        NewCategoryKind = CategoryKinds.First(k => k.Kind == kind);
        MergeSource = null;
        CategoryMessage = null;
        _ = ReloadCategoriesAsync();
    }

    async partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (_loading || value is null || value.Language == Loc.Instance.Language)
        {
            return;
        }

        await _settings.SetLanguageAsync(value.Language);
        Loc.Instance.SetLanguage(value.Language);
    }

    async partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (_loading || value is null || value.Theme == ThemeManager.Current)
        {
            return;
        }

        ThemeManager.Apply(value.Theme); // instant feedback first, then persist
        await _settings.SetThemeAsync(value.Theme);
    }

    [RelayCommand]
    private void OpenDataFolder() => FolderOpener.Open(DataFolder);
}

internal static class FolderOpener
{
    public static void Open(string folder)
    {
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{folder}\"")
                : new ProcessStartInfo("xdg-open", folder);
            start.UseShellExecute = false;
            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Opening a folder is a convenience; the path is shown on screen anyway.
        }
    }
}

/// <summary>Facts about this run of the app, shown in Settings.</summary>
public sealed record AppRuntimeInfo(string AppVersion, int SchemaVersion);
