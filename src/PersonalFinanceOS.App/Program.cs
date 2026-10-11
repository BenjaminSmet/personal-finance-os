using Avalonia;
using PersonalFinanceOS.Infrastructure;
using PersonalFinanceOS.Infrastructure.Database;
using PersonalFinanceOS.Infrastructure.Logging;

namespace PersonalFinanceOS.App;

internal static class Program
{
    public const string SelfCheckArgument = "--self-check";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == SelfCheckArgument)
        {
            return SelfCheck(args[1]);
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer and by headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont() // bundled font: no remote font downloads (spec §2 offline requirement)
            .LogToTrace();

    /// <summary>
    /// Used by the installer test in CI: opens (or creates) the database in the given data folder through the normal
    /// safe startup, including upgrades, and exits without showing a window. 0 means the data opened safely.
    /// </summary>
    internal static int SelfCheck(string dataFolder)
    {
        var paths = new AppDataPaths(dataFolder);
        paths.EnsureDirectories();
        var result = new DatabaseInitializer(paths, new EmbeddedMigrationSource(), new SafeLog(paths.LogsDirectory), AppComposition.AppVersion).Initialize();
        return result.CanOpen ? 0 : 2;
    }
}
