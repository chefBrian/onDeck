using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using OnDeck.App.Platform;
using OnDeck.App.Views;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// Port of <c>FloatingPanel</c> in <c>Views/MenuBarView.swift</c>: an always-on-top, borderless
/// panel showing the same sections as the flyout, draggable by its background, that remembers
/// where it was. It never takes focus — <c>WS_EX_NOACTIVATE</c> is the Windows analogue of
/// <c>.nonactivatingPanel</c>, so clicking it doesn't pull the user out of whatever they were in.
/// </summary>
public partial class FloatingPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    private readonly AppOrchestrator _orchestrator;
    private readonly TeamLogoStore _logos;
    private readonly SettingsStore _settings;

    public FloatingPanelWindow(
        AppOrchestrator orchestrator, TeamLogoStore logos, SettingsStore settings)
    {
        _orchestrator = orchestrator;
        _logos = logos;
        _settings = settings;
        InitializeComponent();

        Sections.RowActivated += ExternalLink.Open;
        Sections.CloseRequested += Hide;
        Sections.Resync = _orchestrator.ResyncRosterAsync;

        _orchestrator.StateChanged += Render;
        _logos.Changed += Render;

        LocationChanged += (_, _) => SaveFrame();
        SizeChanged += (_, _) => SaveFrame();

        IsVisibleChanged += (_, _) => OpenChanged?.Invoke();
    }

    /// <summary>Fires whenever the panel opens or closes, so the Float glyph can follow.</summary>
    public event Action? OpenChanged;

    public bool IsOpen => IsVisible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;

        // Same backdrop treatment as the flyout, including the same open acrylic issue -
        // see windows/ACRYLIC-OPEN-ISSUE.md before changing any of this.
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        DwmBackdrop.RoundCorners(source.Handle);
        if (DwmBackdrop.ApplyAcrylic(source.Handle) != 0)
        {
            Root.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        }

        // Clicking the panel must not steal focus from whatever the user is actually doing.
        var style = GetWindowLong(source.Handle, GwlExStyle);
        SetWindowLong(source.Handle, GwlExStyle, style | WsExNoActivate);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Render();
        RestoreFrame();
        Show();
    }

    private void Render()
    {
        var input = FlyoutInputFactory.From(_orchestrator);

        _logos.Prefetch(FlyoutInputFactory.TeamIds(input));
        Sections.Render(FlyoutSections.Build(input, isFloating: true, _logos.PathFor));
    }

    /// <summary>
    /// <c>isMovableByWindowBackground</c>. Buttons handle their own clicks first, so a press
    /// that reaches here is on the background.
    /// </summary>
    private void OnDragBackground(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
    }

    private void RestoreFrame()
    {
        var workAreas = MonitorWorkArea.AllWorkAreas()
            .Select(area => MonitorWorkArea.ToDips(area, this))
            .ToList();

        if (FloatingPanelPlacement.Restore(_settings.FloatingPanelFrame, workAreas) is { } frame)
        {
            Left = frame.X;
            Top = frame.Y;
            return;
        }

        // Swift's center() fallback.
        var primary = workAreas.Count > 0 ? workAreas[0] : SystemParameters.WorkArea;
        Left = primary.X + ((primary.Width - Width) / 2);
        Top = primary.Y + ((primary.Height - ActualHeight) / 2);
    }

    private void SaveFrame()
    {
        if (!IsVisible || ActualHeight <= 0) return;

        _settings.FloatingPanelFrame = new Rect(Left, Top, Width, ActualHeight);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
