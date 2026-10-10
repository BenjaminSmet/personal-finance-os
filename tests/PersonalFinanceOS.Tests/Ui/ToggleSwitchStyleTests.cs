using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace PersonalFinanceOS.Tests.Ui;

/// <summary>The iOS-style switch: knob slides fully across and the track turns green when on.</summary>
[Collection("UI")]
public class ToggleSwitchStyleTests
{
    [AvaloniaFact]
    public void Knob_slides_inside_the_track_and_track_turns_green()
    {
        var toggle = new ToggleSwitch { Content = "Test" };
        var window = new Window { Content = toggle, Width = 320, Height = 100 };
        window.Show();
        HeadlessSetup.Pump(300);

        var knob = toggle.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_MovingKnobs");
        var track = toggle.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Track");
        var offBrush = track.Background;
        Assert.Equal(0, Canvas.GetLeft(knob));

        toggle.IsChecked = true;
        HeadlessSetup.Pump(500);

        // 51 px track − 27 px knob − 2 × 2 px padding = 20 px of travel; the knob stays fully visible.
        Assert.Equal(20, Canvas.GetLeft(knob));
        Assert.NotEqual(offBrush?.ToString(), track.Background?.ToString());
        Assert.Contains("34c759", track.Background!.ToString()!.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Single(knob.Children.OfType<Ellipse>());
        window.Close();
    }
}
