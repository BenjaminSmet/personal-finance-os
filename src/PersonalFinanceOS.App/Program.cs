using Avalonia;

namespace PersonalFinanceOS.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer and by headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont() // bundled font: no remote font downloads (spec §2 offline requirement)
            .LogToTrace();
}
