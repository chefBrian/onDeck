using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OnDeck.App.Views;

/// <summary>
/// The section list from <c>Views/MenuBarView.swift</c>, shared verbatim by the flyout and the
/// floating panel. It renders a <see cref="FlyoutSections"/> and keeps no state beyond the
/// floating header's refresh button, which is what lets both windows point at the same control
/// with different <see cref="IsFloating"/>.
/// </summary>
public partial class FlyoutContent : UserControl
{
    // Segoe Fluent Icons: Refresh, CheckMark, and Cancel — which serves as both the failed-sync
    // mark and the close button, as it does on the Mac.
    private const string RefreshGlyphText = "";
    private const string DoneGlyphText = "";
    private const string FailedGlyphText = "";
    private const string CloseGlyphText = "";

    private readonly RefreshButtonModel _refresh = new();
    private readonly StackPanel _headerControls;
    private readonly TextBlock _refreshGlyph;

    public FlyoutContent()
    {
        InitializeComponent();

        // Built once and re-parented on each render: the button owns refresh state, so
        // rebuilding it every 10 s would reset a spinner mid-sync.
        (_headerControls, _refreshGlyph) = BuildHeaderControls();
        _refresh.Changed += ShowRefreshState;

        Render(FlyoutSections.Build(new FlyoutInput(), isFloating: false, _ => null));
    }

    /// <summary>Floating mode drops the trailing divider and shows the header controls.</summary>
    public bool IsFloating { get; set; }

    /// <summary>What the header's refresh button runs. Only the floating panel sets it.</summary>
    public Func<Task<bool>>? Resync { get; set; }

    /// <summary>A live row was clicked; the argument is the stream URL to open.</summary>
    public event Action<Uri>? RowActivated;

    /// <summary>The floating panel's close button.</summary>
    public event Action? CloseRequested;

    public void Render(FlyoutSections sections)
    {
        ActiveRows.ItemsSource = sections.Active;
        InGameRows.ItemsSource = sections.InGame;
        UpcomingRows.ItemsSource = sections.Upcoming;
        DoneRows.ItemsSource = sections.Done;

        EmptyText.Text = sections.EmptyText ?? "";
        ErrorText.Text = sections.ErrorText ?? "";

        Show(ActiveSection, sections.ShowsActive);
        Show(InGameSection, sections.ShowsInGame);
        Show(UpcomingSection, sections.ShowsUpcoming);
        Show(DoneSection, sections.ShowsDone);
        Show(EmptySection, sections.ShowsEmpty);
        Show(ErrorSection, sections.ShowsError);

        Show(ActiveDivider, sections.ActiveDivider);
        Show(InGameDivider, sections.InGameDivider);
        Show(UpcomingDivider, sections.UpcomingDivider);
        Show(DoneDivider, sections.DoneDivider);
        Show(EmptyDivider, sections.EmptyDivider);
        Show(ErrorDivider, sections.ErrorDivider);

        PlaceHeaderControls(sections.HeaderControlsSection);
    }

    /// <summary>
    /// Moves the refresh + close pair into whichever header is first on screen — Swift's
    /// <c>showClose</c>. Only one instance exists, so every host is cleared before re-parenting;
    /// WPF throws if an element ends up with two logical parents.
    /// </summary>
    private void PlaceHeaderControls(FlyoutSectionKind section)
    {
        ActiveHeaderControls.Content = null;
        InGameHeaderControls.Content = null;
        UpcomingHeaderControls.Content = null;
        DoneHeaderControls.Content = null;
        EmptyHeaderControls.Content = null;

        if (!IsFloating) return;

        var host = section switch
        {
            FlyoutSectionKind.Active => ActiveHeaderControls,
            FlyoutSectionKind.InGame => InGameHeaderControls,
            FlyoutSectionKind.Upcoming => UpcomingHeaderControls,
            FlyoutSectionKind.Done => DoneHeaderControls,
            _ => EmptyHeaderControls,
        };

        host.Content = _headerControls;
    }

    /// <summary>Port of <c>FloatingRefreshButton</c> plus the close button beside it.</summary>
    private (StackPanel Panel, TextBlock RefreshGlyph) BuildHeaderControls()
    {
        var icons = (FontFamily)Resources["IconFont"];

        var refreshGlyph = NewGlyph(RefreshGlyphText, icons);
        var refresh = new Button
        {
            Content = refreshGlyph,
            Padding = new Thickness(4, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "Refresh",
        };
        refresh.Click += (_, _) =>
        {
            if (Resync is { } resync) _ = _refresh.ClickAsync(resync);
        };

        var close = new Button
        {
            Content = NewGlyph(CloseGlyphText, icons),
            Padding = new Thickness(4, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "Close",
        };
        close.Click += (_, _) => CloseRequested?.Invoke();

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(refresh);
        panel.Children.Add(close);
        return (panel, refreshGlyph);
    }

    /// <summary>A glyph run in the icon font, tinted secondary like every other chrome label.</summary>
    private static TextBlock NewGlyph(string text, FontFamily icons)
    {
        var glyph = new TextBlock { Text = text, FontFamily = icons, FontSize = 12 };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, ThemePalette.TextSecondary);
        return glyph;
    }

    /// <summary>
    /// The click handler runs on the Dispatcher, so <c>ClickAsync</c>'s continuations return to
    /// it and this can touch the glyph directly.
    /// </summary>
    private void ShowRefreshState() => _refreshGlyph.Text = _refresh.State switch
    {
        RefreshButtonState.Done => DoneGlyphText,
        RefreshButtonState.Failed => FailedGlyphText,
        _ => RefreshGlyphText,
    };

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not LiveRowViewModel row) return;
        if (row.StreamUrl is not { } url) return;

        RowActivated?.Invoke(url);
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
