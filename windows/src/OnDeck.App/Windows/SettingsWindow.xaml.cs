using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using OnDeck.App.Platform;
using OnDeck.App.Views;
using OnDeck.Core;

namespace OnDeck.App.Windows;

/// <summary>
/// Port of <c>Views/SettingsView.swift</c>: the Fantrax roster URL and team picker, sync status
/// and Sync Now, the display and notification toggles, and the GitHub links.
/// <para>
/// The toggles two-way bind to a <see cref="SettingsEditor"/>; everything else is rendered from a
/// <see cref="SettingsFormState"/>. Neither the URL box nor a checkbox is ever written by
/// <see cref="Render"/> — a re-render is triggered by the very writes those controls make, so
/// writing back would fight the user mid-edit.
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppOrchestrator _orchestrator;
    private readonly ISettingsStore _settings;
    private readonly SettingsEditor _editor;

    /// <summary>
    /// Set while <see cref="Render"/> assigns the picker, whose own SelectionChanged would
    /// otherwise write the placeholder back over a real selection.
    /// </summary>
    private bool _isRendering;

    public SettingsWindow(AppOrchestrator orchestrator, ISettingsStore settings)
    {
        _orchestrator = orchestrator;
        _settings = settings;
        _editor = new SettingsEditor(settings);

        InitializeComponent();

        DataContext = _editor;

        // Swift's didSet: every write re-reads settings and rebuilds the lists locally.
        _editor.Changed += _orchestrator.SettingsChanged;
        _orchestrator.StateChanged += Render;

        // Seeded once. Render must never touch this again - see the class comment.
        RosterUrlBox.Text = _editor.RosterUrl;

        // A window closed with text typed but never submitted should still keep it.
        Closing += (_, _) => CommitRosterUrl();

        Closed += (_, _) =>
        {
            _orchestrator.StateChanged -= Render;
            _editor.Changed -= _orchestrator.SettingsChanged;
        };

        Render();
    }

    private void Render()
    {
        var state = SettingsFormState.Build(
            SettingsInputFactory.From(_orchestrator, _settings), DateTimeOffset.Now);

        _isRendering = true;
        try
        {
            if (!state.TeamOptions.SequenceEqual(
                    TeamPicker.ItemsSource as IEnumerable<TeamOption> ?? []))
            {
                TeamPicker.ItemsSource = state.TeamOptions;
            }

            TeamPicker.SelectedValue = state.SelectedTeamOptionId;
        }
        finally
        {
            _isRendering = false;
        }

        Show(LoadingTeamsRow, state.ShowsLoadingTeams);
        Show(TeamPickerRow, state.ShowsTeamPicker);
        Show(LoadTeamsButton, state.ShowsLoadTeamsButton);

        TeamsErrorText.Text = state.TeamsErrorText ?? "";
        Show(TeamsErrorText, state.ShowsTeamsError);

        Show(SyncSpinner, state.ShowsSyncSpinner);
        SyncStatusText.Text = state.SyncStatusText ?? "";
        Show(SyncStatusText, state.ShowsSyncStatus);
        SyncNowButton.IsEnabled = state.IsSyncNowEnabled;

        SyncErrorText.Text = state.SyncErrorText ?? "";
        Show(SyncErrorText, state.ShowsSyncError);

        PlayerCountText.Text = state.PlayerCountText ?? "";
        Show(PlayerCountText, state.ShowsPlayerCount);
    }

    /// <summary>
    /// Swift's <c>.onSubmit</c>. The URL is committed first: <c>ParsedLeagueId</c> reads it back
    /// off the store, so fetching before the write would use the previous URL.
    /// </summary>
    private void OnRosterUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitRosterUrl();
        _ = _orchestrator.FetchTeamsAsync();
    }

    private void OnRosterUrlLostFocus(object sender, RoutedEventArgs e) => CommitRosterUrl();

    private void CommitRosterUrl() => _editor.RosterUrl = RosterUrlBox.Text;

    private void OnTeamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRendering) return;

        _editor.SelectedTeamId = TeamPicker.SelectedValue as string ?? "";
    }

    private void OnLoadTeams(object sender, RoutedEventArgs e) => _ = _orchestrator.FetchTeamsAsync();

    private void OnSyncNow(object sender, RoutedEventArgs e) => _ = _orchestrator.ResyncRosterAsync();

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        ExternalLink.Open(e.Uri);
        e.Handled = true;
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
