using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using PersonalFinanceOS.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(HeadlessSetup))]

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>Runs the real app UI without a screen, rendering with Skia so screenshots can be taken.</summary>
public static class HeadlessSetup
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<PersonalFinanceOS.App.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Processes UI work (including async loads) until the condition holds.</summary>
    public static void PumpUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("UI did not reach the expected state.");
            }

            Tick();
        }

        Tick();
    }

    /// <summary>Runs queued UI work and advances the animation clock (headless has no real render timer).</summary>
    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        Thread.Sleep(10);
    }

    public static void Pump(int milliseconds = 200)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            Tick();
        }
    }
}
