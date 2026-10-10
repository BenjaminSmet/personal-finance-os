using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Infrastructure;

namespace PersonalFinanceOS.App.ViewModels;

public sealed record LanguageOption(AppLanguage Language, string Name);

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
    private bool _loading;

    public SettingsViewModel(AppSettingsService settings, AppDataPaths paths, AppRuntimeInfo runtime)
    {
        _settings = settings;
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

    public override Task ActivateAsync()
    {
        _loading = true;
        SelectedLanguage = Languages.First(l => l.Language == Loc.Instance.Language);
        SelectedTheme = Themes.First(t => t.Theme == ThemeManager.Current);
        _loading = false;
        return Task.CompletedTask;
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
