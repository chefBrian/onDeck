using System.Runtime.InteropServices;
using System.Windows;

namespace OnDeck.App.Platform;

/// <summary>
/// Where to anchor the flyout.
/// <para>
/// The exact icon rectangle would come from <c>Shell_NotifyIconGetRect</c>, but that needs the
/// window handle and icon id Hardcodet keeps private (<c>messageSink</c>, <c>iconData</c>), and
/// reflecting into a library's internals is a worse bug than a few pixels. The cursor is over
/// the icon whenever the user clicks it, so it anchors just as well — and it follows the tray
/// across docked taskbars and monitors with no extra work.
/// </para>
/// </summary>
public static class TrayGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>Cursor position in <em>device pixels</em>. Callers must convert to DIPs.</summary>
    public static Point? CursorPosition() =>
        GetCursorPos(out var point) ? new Point(point.X, point.Y) : null;
}
