using Avalonia.Controls;

namespace PersonalFinanceOS.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        GlassWindow.Apply(this, WindowBackdrop);
    }
}

/// <summary>
/// Asks Windows 11 for Mica (or Acrylic) so the window behaves like frosted glass.
/// Falls back silently to an opaque backdrop where the system does not support it.
/// </summary>
internal static class GlassWindow
{
    public static void Apply(Window window, Backdrop backdrop)
    {
        window.TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.AcrylicBlur,
            WindowTransparencyLevel.None,
        ];

        void Update() => backdrop.SetWindowIsTranslucent(window.ActualTransparencyLevel != WindowTransparencyLevel.None);

        window.Opened += (_, _) => Update();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ActualTransparencyLevelProperty)
            {
                Update();
            }
        };
    }
}
