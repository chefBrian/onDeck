using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace OnDeck.App.Platform;

/// <summary>
/// The work area of the monitor a point falls on. <c>SystemParameters.WorkArea</c> only ever
/// describes the primary monitor, which puts the flyout on the wrong screen for a tray on any
/// other one.
/// </summary>
public static class MonitorWorkArea
{
    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Work area in <em>device pixels</em>, or null if the shell won't say.</summary>
    public static Rect? ForDevicePoint(Point devicePixels)
    {
        var point = new NativePoint { X = (int)devicePixels.X, Y = (int)devicePixels.Y };
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return null;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;

        var work = info.WorkArea;
        return new Rect(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    /// <summary>
    /// Device pixels to DIPs, using the visual's own composition target so a per-monitor DPI
    /// setup converts against the right scale.
    /// </summary>
    public static Rect ToDips(Rect devicePixels, Visual visual)
    {
        if (PresentationSource.FromVisual(visual)?.CompositionTarget is not { } target)
        {
            return devicePixels;
        }

        var topLeft = target.TransformFromDevice.Transform(devicePixels.TopLeft);
        var bottomRight = target.TransformFromDevice.Transform(devicePixels.BottomRight);
        return new Rect(topLeft, bottomRight);
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref NativeRect rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    /// <summary>Every connected monitor's work area, in device pixels.</summary>
    public static IReadOnlyList<Rect> AllWorkAreas()
    {
        var areas = new List<Rect>();

        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref NativeRect _, IntPtr _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var work = info.WorkArea;
                    areas.Add(new Rect(
                        work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
                }

                return true;
            },
            IntPtr.Zero);

        return areas;
    }
}
