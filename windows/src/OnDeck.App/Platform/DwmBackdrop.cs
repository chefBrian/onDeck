using System.Runtime.InteropServices;

namespace OnDeck.App.Platform;

/// <summary>
/// Acrylic and rounded corners straight from DWM — what the original plan wanted WPF-UI for.
/// Both attributes are Win11-version-sensitive, so each call returns its HRESULT and the caller
/// falls back to a solid brush.
/// </summary>
public static class DwmBackdrop
{
    private const int SystemBackdropType = 38;      // DWMWA_SYSTEMBACKDROP_TYPE
    private const int CornerPreference = 33;        // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int TransientWindow = 3;          // DWMSBT_TRANSIENTWINDOW (acrylic)
    private const int RoundedCorners = 2;           // DWMWCP_ROUND

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>HRESULT; 0 means the backdrop took.</summary>
    public static int ApplyAcrylic(IntPtr handle)
    {
        // The backdrop is drawn behind the client area, so the frame has to be extended over the
        // whole window first - otherwise DWM composites it somewhere the app never shows.
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(handle, ref margins);

        var value = TransientWindow;
        var result = DwmSetWindowAttribute(handle, SystemBackdropType, ref value, sizeof(int));

        // DWM recalculates a window's frame on a frame change, not on a WPF repaint. Both
        // attributes above return S_OK and still composite nothing until that happens - which is
        // why resizing the window (what hitting Refresh does, by changing the row count) was the
        // only thing that made the acrylic appear. This is that resize without the resize.
        SetWindowPos(
            handle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        return result;
    }

    /// <summary>HRESULT; 0 means the corners were rounded.</summary>
    public static int RoundCorners(IntPtr handle)
    {
        var value = RoundedCorners;
        return DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
    }
}
