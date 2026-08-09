using System.Windows;

namespace OnDeck.App.Windows;

/// <summary>
/// Places the flyout against the tray icon. The taskbar edge is inferred from where the tray
/// icon sits relative to the work area rather than asked for directly, so a docked-left or
/// docked-top taskbar needs no special case at the call site.
/// </summary>
public static class FlyoutPositioner
{
    public static Point Place(Rect trayIcon, Rect workArea, Size flyout, double gap = 8)
    {
        var trayCentreX = trayIcon.X + (trayIcon.Width / 2);
        var trayCentreY = trayIcon.Y + (trayIcon.Height / 2);

        double x;
        double y;

        if (trayCentreY >= workArea.Bottom)
        {
            // Taskbar along the bottom: sit above it, right edge aligned with the icon.
            x = trayIcon.Right - flyout.Width;
            y = workArea.Bottom - flyout.Height - gap;
        }
        else if (trayCentreY <= workArea.Top)
        {
            x = trayIcon.Right - flyout.Width;
            y = workArea.Top + gap;
        }
        else if (trayCentreX <= workArea.Left)
        {
            x = workArea.Left + gap;
            y = trayIcon.Bottom - flyout.Height;
        }
        else
        {
            x = workArea.Right - flyout.Width - gap;
            y = trayIcon.Bottom - flyout.Height;
        }

        return new Point(Clamp(x, workArea.Left, workArea.Right, flyout.Width, gap),
                         Clamp(y, workArea.Top, workArea.Bottom, flyout.Height, gap));
    }

    /// <summary>
    /// Keeps the flyout inside the work area. When it simply doesn't fit, the near edge wins —
    /// a window pinned to the top with its bottom off-screen beats one positioned at a negative
    /// coordinate on the wrong monitor.
    /// </summary>
    private static double Clamp(double value, double min, double max, double extent, double gap)
    {
        var upper = max - extent - gap;
        var lower = min + gap;
        if (upper < lower) return Math.Max(min, 0);
        return Math.Clamp(value, lower, upper);
    }
}
