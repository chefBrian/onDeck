using System.Windows;
using OnDeck.App.Windows;

namespace OnDeck.App.Tests;

public class FlyoutPositionerTests
{
    // 1920x1080 with a 48 px taskbar docked at the bottom.
    private static readonly Rect BottomWorkArea = new(0, 0, 1920, 1032);
    private static readonly Size Flyout = new(300, 500);

    [Fact]
    public void Place_PutsTheFlyoutAboveABottomDockedTray()
    {
        var tray = new Rect(1850, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.Equal(1032 - 500 - 8, point.Y);              // above the work area edge
        Assert.True(point.X + 300 <= 1920 - 8);             // right-aligned, inside the screen
    }

    [Fact]
    public void Place_RightAlignsWithTheTrayIcon()
    {
        var tray = new Rect(1850, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.Equal(1874 - 300, point.X);                  // tray right edge minus width
    }

    [Fact]
    public void Place_DropsBelowATopDockedTaskbar()
    {
        var workArea = new Rect(0, 48, 1920, 1032);
        var tray = new Rect(1850, 12, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(48 + 8, point.Y);
    }

    [Fact]
    public void Place_SitsBesideALeftDockedTaskbar()
    {
        var workArea = new Rect(62, 0, 1858, 1080);
        var tray = new Rect(10, 1000, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(62 + 8, point.X);
        Assert.True(point.Y + 500 <= 1080 - 8);
    }

    [Fact]
    public void Place_SitsBesideARightDockedTaskbar()
    {
        var workArea = new Rect(0, 0, 1858, 1080);
        var tray = new Rect(1880, 1000, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.Equal(1858 - 300 - 8, point.X);
    }

    [Fact]
    public void Place_ClampsToTheWorkAreaWhenTheTrayIsNearACorner()
    {
        var tray = new Rect(4, 1040, 24, 24);           // tray icon hard against the left edge

        var point = FlyoutPositioner.Place(tray, BottomWorkArea, Flyout);

        Assert.True(point.X >= 8);
    }

    [Fact]
    public void Place_HandlesAFlyoutTallerThanTheWorkArea()
    {
        var point = FlyoutPositioner.Place(
            new Rect(1850, 1040, 24, 24), BottomWorkArea, new Size(300, 2000));

        Assert.Equal(0, point.Y);                        // pinned to the top, never negative
    }

    [Fact]
    public void Place_UsesTheMonitorTheTrayIsOn()
    {
        // Second monitor to the right: work area origin is not (0,0).
        var workArea = new Rect(1920, 0, 1920, 1032);
        var tray = new Rect(3770, 1040, 24, 24);

        var point = FlyoutPositioner.Place(tray, workArea, Flyout);

        Assert.True(point.X >= 1920);
        Assert.Equal(1032 - 500 - 8, point.Y);
    }
}
