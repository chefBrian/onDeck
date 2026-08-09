using System.Windows;

namespace OnDeck.App.Windows;

/// <summary>
/// Decides whether a remembered panel frame can still be used. macOS gets this from
/// <c>setFrameUsingName</c> returning false; Windows has no equivalent, and a panel restored
/// onto a monitor that has since been unplugged is unreachable — it has no taskbar button and
/// no Alt-Tab entry.
/// </summary>
public static class FloatingPanelPlacement
{
    /// <summary>Enough of the window on screen to grab and drag.</summary>
    private const double MinimumVisibleArea = 300 * 32;

    public static Rect? Restore(Rect? saved, IReadOnlyList<Rect> workAreas)
    {
        if (saved is not { } frame) return null;
        if (frame.Width <= 0 || frame.Height <= 0) return null;

        foreach (var workArea in workAreas)
        {
            var overlap = Rect.Intersect(frame, workArea);
            if (overlap.IsEmpty) continue;
            if (overlap.Width * overlap.Height >= MinimumVisibleArea) return frame;
        }

        return null;
    }
}
