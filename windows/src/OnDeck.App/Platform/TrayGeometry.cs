using System.Runtime.InteropServices;
using System.Windows;

namespace OnDeck.App.Platform;

/// <summary>
/// Asks the shell where our tray icon actually is. Guessing the bottom-right corner breaks on
/// docked taskbars, overflow flyouts and multi-monitor setups.
/// </summary>
public static class TrayGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Item;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(
        ref NotifyIconIdentifier identifier, out NativeRect rectangle);

    /// <summary>Screen-pixel rectangle of the icon, or null when the shell won't say.</summary>
    public static Rect? IconRectangle(IntPtr windowHandle, uint iconId)
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
            Window = windowHandle,
            Id = iconId,
        };

        if (Shell_NotifyIconGetRect(ref identifier, out var rectangle) != 0) return null;

        return new Rect(
            rectangle.Left,
            rectangle.Top,
            rectangle.Right - rectangle.Left,
            rectangle.Bottom - rectangle.Top);
    }
}
