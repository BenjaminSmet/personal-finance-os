using Avalonia.Controls;

namespace PersonalFinanceOS.App.Views;

public partial class StartupProblemWindow : Window
{
    public StartupProblemWindow()
    {
        InitializeComponent();
        GlassWindow.Apply(this, WindowBackdrop);
    }
}
