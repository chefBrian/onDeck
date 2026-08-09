using System.Runtime.InteropServices;

namespace OnDeck.App.Platform;

/// <summary>
/// Acrylic and rounded corners for the flyout and floating panel.
/// <para>
/// The acrylic comes from the accent policy (<c>SetWindowCompositionAttribute</c>), not from
/// Win11's system-backdrop attribute (<c>DWMWA_SYSTEMBACKDROP_TYPE</c>). DWM accepts the latter
/// with <c>S_OK</c> and then composites only its solid fallback colour — for every window of
/// every app on this machine's build (26200.8973), framed or frameless, active or not. The
/// accent path is the one DWM still runs live, and it works for never-activated windows like
/// the panel, which the system-backdrop path never did. Full investigation:
/// <c>windows/ACRYLIC-OPEN-ISSUE.md</c>.
/// </para>
/// </summary>
public static class DwmBackdrop
{
    private const int CornerPreference = 33;        // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int RoundedCorners = 2;           // DWMWCP_ROUND

    private const int AccentPolicyAttribute = 19;   // WCA_ACCENT_POLICY

    // ACCENT_ENABLE_BLURBEHIND: plain gaussian blur of what's behind, no tint layer of its
    // own. Its sibling ACRYLICBLURBEHIND (4) lays the system's acrylic material - a dark,
    // saturating base - over the blur, which swallowed every tint-alpha change the owner
    // tried; the window read as near-opaque at 70%, 30% and 20% alike. Plain blur is the
    // ~95%-see-through the owner asked for.
    private const int BlurBehind = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public uint GradientColor;      // ABGR — ThemePalette.BackdropTintAbgr's byte order
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int Size;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(
        IntPtr window, ref CompositionAttributeData data);

    /// <summary>
    /// 0 on success, a Win32 error (or -1) otherwise — the caller falls back to a solid brush.
    /// The API is undocumented but stable since Win10 1803. <paramref name="tintAbgr"/> is laid
    /// over the blur by WPF-visible builds that honour it; most ignore it for plain blur, which
    /// is fine — the tint is a whisper by design.
    /// </summary>
    public static int ApplyAcrylic(IntPtr handle, uint tintAbgr)
    {
        var accent = new AccentPolicy
        {
            State = BlurBehind,
            Flags = 0,
            GradientColor = tintAbgr,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, fDeleteOld: false);
            var data = new CompositionAttributeData
            {
                Attribute = AccentPolicyAttribute,
                Data = buffer,
                Size = size,
            };

            if (SetWindowCompositionAttribute(handle, ref data) != 0) return 0;

            var error = Marshal.GetLastWin32Error();
            return error == 0 ? -1 : error;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>HRESULT; 0 means the corners were rounded.</summary>
    public static int RoundCorners(IntPtr handle)
    {
        var value = RoundedCorners;
        return DwmSetWindowAttribute(handle, CornerPreference, ref value, sizeof(int));
    }
}
