using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OnDeck.App.Platform;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// The tray flyout. Phase 7 replaces the placeholder content with the real sections; what
/// matters here is that it lands in the right place, dismisses on focus loss, and gets its
/// backdrop from DWM with a solid fallback.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly AppOrchestrator _orchestrator;

    public FlyoutWindow(AppOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
        InitializeComponent();

        Deactivated += (_, _) => Hide();        // light dismiss
        _orchestrator.StateChanged += RenderSummary;
        Closed += (_, _) => _orchestrator.StateChanged -= RenderSummary;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;

        // Load-bearing: DWM draws the backdrop *behind* the window, so WPF's own render surface
        // has to stop painting over it. Without this the acrylic is applied and invisible.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var corners = DwmBackdrop.RoundCorners(source.Handle);
        var acrylic = DwmBackdrop.ApplyAcrylic(source.Handle);

        ShellLog.Append(
            $"[Flyout] backdrop hresult=0x{acrylic:X8} corners=0x{corners:X8} "
            + $"os={Environment.OSVersion.Version}");

        if (acrylic != 0)
        {
            // Older Win11 builds refuse the backdrop attribute - paint something opaque so the
            // flyout is never an unreadable transparent rectangle.
            Root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }
    }

    /// <summary>
    /// Opens the flyout anchored at <paramref name="anchorDevicePixels"/> — the cursor, which is
    /// over the tray icon when the user clicks it.
    /// </summary>
    public void ShowAt(Point? anchorDevicePixels)
    {
        RenderSummary();

        // Measure before placing: SizeToContent means Height is only real after a layout pass.
        Show();
        UpdateLayout();

        var workArea = WorkAreaFor(anchorDevicePixels);
        var anchor = ToAnchorRect(anchorDevicePixels, workArea);

        var placement = FlyoutPositioner.Place(anchor, workArea, new Size(Width, ActualHeight));

        Left = placement.X;
        Top = placement.Y;

        Activate();
    }

    /// <summary>
    /// The work area of the monitor the tray is on, in DIPs. Falls back to the primary monitor's
    /// when there is no anchor or the shell declines to answer.
    /// </summary>
    private Rect WorkAreaFor(Point? anchorDevicePixels)
    {
        if (anchorDevicePixels is { } anchor
            && MonitorWorkArea.ForDevicePoint(anchor) is { } devicePixels)
        {
            return MonitorWorkArea.ToDips(devicePixels, this);
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// Device pixels to DIPs. WPF's <c>Left</c>/<c>Top</c> and the work area are DIPs while
    /// <c>GetCursorPos</c> is raw pixels, so skipping this puts the flyout in the wrong place on
    /// any display not at 100% scaling.
    /// </summary>
    private Rect ToAnchorRect(Point? devicePixels, Rect workArea)
    {
        if (devicePixels is not { } point)
        {
            // No cursor (e.g. a second launch signalling us): fall back to the tray corner.
            return new Rect(workArea.Right - 24, workArea.Bottom, 24, 24);
        }

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            point = target.TransformFromDevice.Transform(point);
        }

        // A small box around the cursor stands in for the icon's own rectangle.
        return new Rect(point.X - 12, point.Y - 12, 24, 24);
    }

    private void RenderSummary()
    {
        SummaryText.Text =
            $"Active {_orchestrator.ActivePlayers.Count}   "
            + $"In game {_orchestrator.InGamePlayers.Count}   "
            + $"Upcoming {_orchestrator.UpcomingPlayers.Count}   "
            + $"Done {_orchestrator.DonePlayers.Count}"
            + (_orchestrator.SyncError is { } error ? $"\n{error}" : "");
    }
}
