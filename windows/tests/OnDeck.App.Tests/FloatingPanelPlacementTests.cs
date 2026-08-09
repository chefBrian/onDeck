using System.Windows;
using OnDeck.App.Windows;

namespace OnDeck.App.Tests;

public class FloatingPanelPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Secondary = new(1920, 0, 2560, 1400);

    [Fact]
    public void RestoresAFrameFullyOnAMonitor()
    {
        var saved = new Rect(400, 300, 300, 500);

        Assert.Equal(saved, FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RestoresAFrameOnASecondMonitor()
    {
        var saved = new Rect(2200, 100, 300, 500);

        Assert.Equal(saved, FloatingPanelPlacement.Restore(saved, [Primary, Secondary]));
    }

    [Fact]
    public void RejectsAFrameOnAMonitorThatIsGone()
    {
        var saved = new Rect(2200, 100, 300, 500);

        Assert.Null(FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RejectsAFrameOnlyBarelyOverlapping()
    {
        // Two pixels of the corner on screen is not a reachable window.
        var saved = new Rect(1918, 1038, 300, 500);

        Assert.Null(FloatingPanelPlacement.Restore(saved, [Primary]));
    }

    [Fact]
    public void RejectsNothingSaved()
    {
        Assert.Null(FloatingPanelPlacement.Restore(null, [Primary]));
    }

    [Fact]
    public void RejectsAnEmptyFrame()
    {
        Assert.Null(FloatingPanelPlacement.Restore(new Rect(0, 0, 0, 0), [Primary]));
    }
}
