using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OnDeck.App.Platform;
using OnDeck.App.Views;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// The tray flyout: the sections from <c>Views/MenuBarView.swift</c> over a footer, anchored to
/// the tray icon, dismissed on focus loss, with its backdrop from DWM and a solid fallback.
/// </summary>
public partial class FlyoutWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly TeamLogoStore _logos;

    public FlyoutWindow(AppOrchestrator orchestrator, TeamLogoStore logos)
    {
        _orchestrator = orchestrator;
        _logos = logos;
        InitializeComponent();

        Deactivated += (_, _) => Hide();        // light dismiss

        Sections.RowActivated += OpenStream;
        Footer.Resync = _orchestrator.ResyncRosterAsync;
        Footer.FantraxRequested += OpenFantrax;
        Footer.QuitRequested += () => Application.Current.Shutdown();
        Footer.FloatRequested += () => FloatRequested?.Invoke();

        _orchestrator.StateChanged += Render;
        _logos.Changed += Render;
        Closed += (_, _) =>
        {
            _orchestrator.StateChanged -= Render;
            _logos.Changed -= Render;
        };
    }

    /// <summary>The footer's Float button; the app owns the panel itself.</summary>
    public event Action? FloatRequested;

    /// <summary>Keeps the Float glyph in step with whether the panel is open.</summary>
    public void SetFloating(bool isPanelOpen) => Footer.SetFloating(isPanelOpen);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop("init");

        // A resize is the one thing observed to make the backdrop appear (hitting Refresh changes
        // the row count, which resizes the window). Re-assert on every resize so we find out
        // whether the resize or the repaint is the active ingredient.
        SizeChanged += (_, _) => ApplyBackdrop("resize");
    }

    /// <summary>
    /// Clears WPF's own opaque render surface and asks DWM for the acrylic backdrop.
    /// <para>
    /// Called on every open, not just at init, and that repetition is the fix rather than
    /// belt-and-braces. <c>CompositionTarget.BackgroundColor</c> is a property of the composition
    /// target, and <c>SizeToContent</c> makes the window resize right after
    /// <c>OnSourceInitialized</c> — the target is rebuilt and comes back **opaque**, painting over
    /// a backdrop DWM had already accepted. That is why the attribute always returned
    /// <c>S_OK</c> while the flyout looked solid, and why re-rendering (hitting Refresh) made it
    /// translucent. See <c>windows/ACRYLIC-OPEN-ISSUE.md</c>.
    /// </para>
    /// </summary>
    private void ApplyBackdrop(string phase)
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;

        var before = source.CompositionTarget.BackgroundColor;
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        var corners = DwmBackdrop.RoundCorners(source.Handle);
        var acrylic = DwmBackdrop.ApplyAcrylic(source.Handle);

        // Setting the colour only affects the *next* render pass. Without forcing one, the
        // surface keeps the opaque pixels from the paint that already happened - which is the
        // difference between this and hitting Refresh, whose content change repaints anyway.
        Root.InvalidateVisual();

        ShellLog.Append(
            $"[Flyout/{phase}] bgWas={before} size={ActualWidth:F0}x{ActualHeight:F0} "
            + $"visible={IsVisible} hr=0x{acrylic:X8} corners=0x{corners:X8}");

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
        Render();

        // Show first: the device-pixel to DIP conversion below needs a composition target, and
        // there isn't one until the window has an hwnd.
        Show();

        var workArea = WorkAreaFor(anchorDevicePixels);

        // MenuBarView has no scroll view — the Mac window simply grows. Cap on the monitor
        // instead of an arbitrary constant so scrolling only starts when the roster genuinely
        // outgrows the screen, not at some fixed row count.
        MaxHeight = Math.Max(200, workArea.Height - 16);

        // Measure before placing: SizeToContent means Height is only real after a layout pass.
        UpdateLayout();

        var anchor = ToAnchorRect(anchorDevicePixels, workArea);

        var placement = FlyoutPositioner.Place(anchor, workArea, new Size(Width, ActualHeight));

        Left = placement.X;
        Top = placement.Y;

        // After the resize above, not before: the composition target has just been rebuilt and
        // has reverted to an opaque background.
        ApplyBackdrop("show");

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

    private void Render()
    {
        var input = FlyoutInputFactory.From(_orchestrator);

        _logos.Prefetch(FlyoutInputFactory.TeamIds(input));
        Sections.Render(FlyoutSections.Build(input, isFloating: false, _logos.PathFor));

        Footer.ShowsFantrax = _orchestrator.ParsedLeagueId is not null;
    }

    private void OpenStream(Uri url)
    {
        Hide();     // Swift dismisses the menu bar window before opening the link
        ExternalLink.Open(url);
    }

    private void OpenFantrax()
    {
        if (_orchestrator.ParsedLeagueId is not { } leagueId) return;

        Hide();
        ExternalLink.Open(new Uri($"https://www.fantrax.com/fantasy/league/{leagueId}/home"));
    }
}
