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

public sealed partial class CategoryRowViewModel(Category category, Func<CategoryRowViewModel, Task> rename, Func<CategoryRowViewModel, Task> archive) : ViewModelBase
{
    public Category Category { get; } = category;

    public string Name => Category.Name;

    public string KindName => Loc.T("CategoryKind_" + Category.Kind);

    public bool IsLast { get; set; }

    [RelayCommand]
    private Task Rename() => rename(this);

    [RelayCommand]
    private Task Archive() => archive(this);
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

    // ===== Categories (spec §13: create, rename, archive) =====

    public System.Collections.ObjectModel.ObservableCollection<CategoryRowViewModel> CategoryRows { get; } = [];

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

    private Task StartRenameAsync(CategoryRowViewModel row)
    {
        _renaming = row;
        IsRenaming = true;
        NewCategoryName = row.Name;
        NewCategoryKind = CategoryKinds.First(k => k.Kind == row.Category.Kind);
        return Task.CompletedTask;
    }

    private async Task ArchiveCategoryAsync(CategoryRowViewModel row)
    {
        await _categories.ArchiveAsync(row.Category.Id);
        await ReloadCategoriesAsync();
    }

    private async Task ReloadCategoriesAsync()
    {
        var list = await _categories.ListAsync();
        CategoryRows.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            CategoryRows.Add(new CategoryRowViewModel(list[i], StartRenameAsync, ArchiveCategoryAsync) { IsLast = i == list.Count - 1 });
        }
    }

    private static List<CategoryKindOption> CategoryKindOptions() =>
        [.. Enum.GetValues<CategoryKind>().Select(k => new CategoryKindOption(k, Loc.T("CategoryKind_" + k)))];

    protected override void OnLanguageChanged()
    {
        var kind = NewCategoryKind.Kind;
        CategoryKinds = CategoryKindOptions();
        NewCategoryKind = CategoryKinds.First(k => k.Kind == kind);
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
