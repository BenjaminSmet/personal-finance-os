using Avalonia.Controls;

namespace PersonalFinanceOS.App.Views;

public partial class Backdrop : UserControl
{
    public Backdrop() => InitializeComponent();

    /// <summary>
    /// When the window has real Mica/Acrylic transparency, let the desktop show through a little.
    /// Without it (Windows 10, tests) the backdrop is fully opaque so the glass still has colour behind it.
    /// </summary>
    public void SetWindowIsTranslucent(bool translucent) =>
        Base.Opacity = translucent ? 0.72 : 1.0;
}
