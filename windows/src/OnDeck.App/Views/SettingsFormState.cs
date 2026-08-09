using OnDeck.Core;
using OnDeck.Core.Networking;

namespace OnDeck.App.Views;

/// <summary>
/// One row of the team picker. The placeholder carries an empty id, mirroring Swift's
/// <c>Text("Select a team...").tag("")</c>.
/// </summary>
public sealed record TeamOption(string Id, string Name);

/// <summary>
/// Everything <c>Views/SettingsView.swift</c> reads off <c>AppState</c>, as plain values. Taking
/// this rather than the orchestrator keeps the form's branch rules testable without standing up
/// the engine.
/// </summary>
public sealed record SettingsInput
{
    public string RosterUrl { get; init; } = "";
    public string? ParsedLeagueId { get; init; }
    public bool UrlHasTeamId { get; init; }
    public string? SelectedTeamId { get; init; }

    /// <summary><c>effectiveTeamID != nil</c> — the Sync Now guard.</summary>
    public bool HasEffectiveTeam { get; init; }

    public IReadOnlyList<FantraxTeam> AvailableTeams { get; init; } = [];
    public bool IsLoadingTeams { get; init; }
    public string? TeamsError { get; init; }
    public bool IsSyncing { get; init; }
    public DateTimeOffset? LastSyncDate { get; init; }
    public string? SyncError { get; init; }
    public int LoadedPlayerCount { get; init; }
}

/// <summary>
/// The laid-out settings form: which sub-controls appear and what each label says. Port of the
/// conditional structure in <c>Views/SettingsView.swift:13-79</c>.
/// </summary>
public sealed record SettingsFormState
{
    /// <summary>The id of Swift's <c>Text("Select a team...").tag("")</c>.</summary>
    public const string PlaceholderTeamId = "";

    public bool ShowsLoadingTeams { get; init; }
    public bool ShowsTeamPicker { get; init; }
    public bool ShowsLoadTeamsButton { get; init; }
    public string? TeamsErrorText { get; init; }
    public bool ShowsTeamsError => TeamsErrorText is not null;

    public IReadOnlyList<TeamOption> TeamOptions { get; init; } = [];
    public string SelectedTeamOptionId { get; init; } = PlaceholderTeamId;

    public bool ShowsSyncSpinner { get; init; }
    public string? SyncStatusText { get; init; }
    public bool ShowsSyncStatus => SyncStatusText is not null;
    public bool IsSyncNowEnabled { get; init; }

    public string? SyncErrorText { get; init; }
    public bool ShowsSyncError => SyncErrorText is not null;

    public string? PlayerCountText { get; init; }
    public bool ShowsPlayerCount => PlayerCountText is not null;

    public static SettingsFormState Build(SettingsInput input, DateTimeOffset now)
    {
        // Swift wraps the picker, its loading row, the Load Teams button AND the teams error in
        // one `if !rosterURL.isEmpty && !urlHasTeamID` (SettingsView.swift:20-46).
        var needsPicker = input.RosterUrl.Length > 0 && !input.UrlHasTeamId;
        var hasTeams = input.AvailableTeams.Count > 0;

        List<TeamOption> options = [new(PlaceholderTeamId, "Select a team...")];
        options.AddRange(input.AvailableTeams.Select(team => new TeamOption(team.Id, team.Name)));

        // A selection that isn't among the options leaves a ComboBox blank with no clue why;
        // fall back to the placeholder, which is what the user is being asked to replace.
        var selected = input.SelectedTeamId ?? PlaceholderTeamId;
        if (options.All(option => option.Id != selected)) selected = PlaceholderTeamId;

        return new SettingsFormState
        {
            ShowsLoadingTeams = needsPicker && input.IsLoadingTeams,
            ShowsTeamPicker = needsPicker && !input.IsLoadingTeams && hasTeams,
            ShowsLoadTeamsButton = needsPicker && !input.IsLoadingTeams && !hasTeams
                                   && input.ParsedLeagueId is not null,
            TeamsErrorText = needsPicker ? NullIfBlank(input.TeamsError) : null,

            TeamOptions = options,
            SelectedTeamOptionId = selected,

            ShowsSyncSpinner = input.IsSyncing,
            SyncStatusText = input.IsSyncing
                ? "Syncing..."
                : input.LastSyncDate is { } date
                    ? $"Last synced: {RelativeTime.Describe(date, now)} ago"
                    : null,
            IsSyncNowEnabled = !input.IsSyncing && input.HasEffectiveTeam,

            SyncErrorText = NullIfBlank(input.SyncError),
            PlayerCountText = input.LoadedPlayerCount > 0
                ? $"{input.LoadedPlayerCount} players loaded"
                : null,
        };
    }

    private static string? NullIfBlank(string? text) =>
        string.IsNullOrEmpty(text) ? null : text;
}

/// <summary>
/// Reads a <see cref="SettingsInput"/> off the orchestrator and the store. The one place the
/// settings window touches Core, mirroring <see cref="FlyoutInputFactory"/>.
/// </summary>
public static class SettingsInputFactory
{
    public static SettingsInput From(AppOrchestrator orchestrator, ISettingsStore settings) => new()
    {
        RosterUrl = settings.RosterUrl ?? "",
        ParsedLeagueId = orchestrator.ParsedLeagueId,
        UrlHasTeamId = orchestrator.UrlHasTeamId,
        SelectedTeamId = settings.SelectedTeamId,
        HasEffectiveTeam = orchestrator.EffectiveTeamId is not null,
        AvailableTeams = orchestrator.AvailableTeams,
        IsLoadingTeams = orchestrator.IsLoadingTeams,
        TeamsError = orchestrator.TeamsError,
        IsSyncing = orchestrator.IsSyncing,
        LastSyncDate = orchestrator.LastSyncDate,
        SyncError = orchestrator.SyncError,
        LoadedPlayerCount = orchestrator.LoadedPlayerCount,
    };
}
