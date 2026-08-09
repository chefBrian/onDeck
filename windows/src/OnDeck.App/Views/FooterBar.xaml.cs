using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace OnDeck.App.Views;

/// <summary>
/// Port of <c>FooterButtons</c> in <c>Views/MenuBarView.swift</c>. Settings is absent by design
/// until Phase 8 brings the window it would open.
/// </summary>
public partial class FooterBar : UserControl
{
    // Segoe Fluent Icons: Refresh, CheckMark, Cancel, OpenInNewWindow, BackToWindow.
    private const string RefreshGlyphText = "";
    private const string DoneGlyphText = "";
    private const string FailedGlyphText = "";
    private const string FloatOpenGlyphText = "";
    private const string FloatCloseGlyphText = "";

    private readonly RefreshButtonModel _refresh = new();
    private readonly Storyboard _spinner;

    public FooterBar()
    {
        InitializeComponent();

        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Storyboard.SetTarget(spin, RefreshGlyph);
        Storyboard.SetTargetProperty(
            spin, new PropertyPath("RenderTransform.(RotateTransform.Angle)"));
        _spinner = new Storyboard();
        _spinner.Children.Add(spin);

        _refresh.Changed += ShowRefreshState;
    }

    /// <summary>Hidden when the roster URL has no parseable leagueID — Swift's <c>if let leagueID</c>.</summary>
    public bool ShowsFantrax
    {
        get => FantraxButton.Visibility == Visibility.Visible;
        set => FantraxButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What Refresh runs. Set by the window that owns this bar.</summary>
    public Func<Task<bool>>? Resync { get; set; }

    public event Action? FantraxRequested;

    public event Action? FloatRequested;

    public event Action? QuitRequested;

    /// <summary>Swaps the Float glyph between "open a panel" and "put it back".</summary>
    public void SetFloating(bool isPanelOpen) =>
        FloatGlyph.Text = isPanelOpen ? FloatCloseGlyphText : FloatOpenGlyphText;

    private void OnFantrax(object sender, RoutedEventArgs e) => FantraxRequested?.Invoke();

    private void OnFloat(object sender, RoutedEventArgs e) => FloatRequested?.Invoke();

    private void OnQuit(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (Resync is not { } resync) return;
        _ = _refresh.ClickAsync(resync);
    }

    /// <summary>
    /// The click handler runs on the Dispatcher, so <c>ClickAsync</c>'s continuations return to
    /// it and this can touch the glyph and the storyboard directly.
    /// </summary>
    private void ShowRefreshState()
    {
        switch (_refresh.State)
        {
            case RefreshButtonState.Spinning:
                RefreshGlyph.Text = RefreshGlyphText;
                _spinner.Begin(this, isControllable: true);
                break;

            case RefreshButtonState.Done:
                _spinner.Stop(this);
                RefreshGlyph.Text = DoneGlyphText;
                break;

            case RefreshButtonState.Failed:
                _spinner.Stop(this);
                RefreshGlyph.Text = FailedGlyphText;
                break;

            default:
                _spinner.Stop(this);
                RefreshGlyph.Text = RefreshGlyphText;
                break;
        }
    }
}
