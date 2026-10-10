using Avalonia.Controls;
using Avalonia.Input;

namespace PersonalFinanceOS.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        GlassWindow.Apply(this, WindowBackdrop);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.K, KeyModifiers.Control), Command = new FocusQuickAddCommand(this) });
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.QuickAdd.FocusRequested += (_, _) => FocusQuickAdd();
            }
        };
    }

    /// <summary>Ctrl+K: start typing a transaction from any page (keyboard navigation, spec §20).</summary>
    public void FocusQuickAdd()
    {
        QuickAddBox.Focus();
        QuickAddBox.SelectAll();
    }

    private sealed class FocusQuickAddCommand(MainWindow window) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => window.FocusQuickAdd();
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
