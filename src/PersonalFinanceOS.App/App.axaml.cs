using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PersonalFinanceOS.App.Localization;
using PersonalFinanceOS.App.ViewModels;
using PersonalFinanceOS.App.Views;
using PersonalFinanceOS.Application.Categories;
using PersonalFinanceOS.Application.Settings;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Logging;

namespace PersonalFinanceOS.App;

public partial class App : Avalonia.Application
{
    private ServiceProvider? _services;

    /// <summary>Raised with the new window after the app reopened on restored data.</summary>
    public event Action<Window>? Restarted;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = CreateStartWindow(AppDataPaths.ForCurrentUser(), desktop.Shutdown);
            desktop.Exit += (_, _) => _services?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Opens the database safely and returns either the main window or a recovery screen.
    /// Public so headless UI tests can start the real app against a temporary data folder.
    /// </summary>
    public Window CreateStartWindow(AppDataPaths paths, Action<int> shutdown)
    {
        var log = new SafeLog(paths.LogsDirectory);
        log.Info("App starting, version " + AppComposition.AppVersion);
        var initializer = new DatabaseInitializer(paths, new EmbeddedMigrationSource(), log, AppComposition.AppVersion);
        var startup = initializer.Initialize();

        if (startup.CanOpen)
        {
            return CreateMainWindow(paths, startup, shutdown);
        }

        Loc.Instance.SetLanguage(Loc.FromSystem());
        var window = new StartupProblemWindow();
        window.DataContext = new StartupProblemViewModel(
            startup,
            paths,
            initializer.StartNewProfileAfterMissingDatabase,
            recovered =>
            {
                var main = CreateMainWindow(paths, recovered, shutdown);
                if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.MainWindow = main;
                }

                main.Show();
                window.Close();
            },
            () => shutdown(1));
        return window;
    }

    private Window CreateMainWindow(AppDataPaths paths, DatabaseStartupResult startup, Action<int> shutdown)
    {
        _services?.Dispose();
        _services = AppComposition.Build(paths, startup);

        var settings = _services.GetRequiredService<AppSettingsService>();
        // Run off the UI thread so the blocking wait cannot deadlock on the UI synchronization context.
        var language = Task.Run(() => settings.GetLanguageAsync()).GetAwaiter().GetResult();
        Loc.Instance.SetLanguage(language);
        ThemeManager.Apply(Task.Run(() => settings.GetThemeAsync()).GetAwaiter().GetResult());

        // Starter categories, once, in the language the user has now. They are ordinary, editable data afterwards.
        var categories = _services.GetRequiredService<CategoryService>();
        Task.Run(() => categories.EnsureDefaultsAsync(DefaultCategories.Localized())).GetAwaiter().GetResult();

        var viewModel = _services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        window.Opened += async (_, _) => await viewModel.InitializeAsync();

        // After a restore the app starts again on the restored data, through the same safe startup checks (which
        // also upgrade a backup made by an older version).
        _services.GetRequiredService<BackupViewModel>().Restored = () =>
        {
            var next = CreateStartWindow(paths, shutdown);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = next;
            }

            next.Show();
            window.Close();
            Restarted?.Invoke(next);
        };
        return window;
    }
}
