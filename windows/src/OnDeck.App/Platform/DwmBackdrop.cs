using System.Runtime.InteropServices;

namespace OnDeck.App.Platform;

/// <summary>
/// Acrylic and rounded corners straight from DWM — what the original plan wanted WPF-UI for.
/// Both attributes are Win11-version-sensitive, so the acrylic call reports whether it took and
/// the caller falls back to a solid brush.
/// </summary>
public static class DwmBackdrop
{
    private const int SystemBackdropType = 38;      // DWMWA_SYSTEMBACKDROP_TYPE
    private const int CornerPreference = 33;        // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int TransientWindow = 3;          // DWMSBT_TRANSIENTWINDOW (acrylic)
    private const int RoundedCorners = 2;           // DWMWCP_ROUND

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    public static bool TryApplyAcrylic(IntPtr handle)
    {
        var value = TransientWindow;
        return DwmSetWindowAttribute(handle, SystemBackdropType, ref value, sizeof(int)) == 0;
    }

    public static void RoundCorners(IntPtr handle)
    {
        var value = RoundedCorners;
        DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
    }
}
