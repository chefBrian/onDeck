using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core;

/// <summary>
/// Port of the portable half of <c>App/AppState.swift</c>: owns the managers, publishes the
/// four player lists as immutable snapshots, and drives roster/schedule refreshes.
/// Everything here runs on one logical thread — the WPF <c>Dispatcher</c> in the app, a
/// pumped single-threaded context in tests — which is what makes the coalesced list rebuild
/// and the post-await race guards correct. No <c>ConfigureAwait(false)</c> anywhere.
/// </summary>
public sealed class AppOrchestrator
{
    private readonly RosterManager _roster;
    private readonly ScheduleManager _schedule;
    private readonly GameMonitor _monitor;
    private readonly StateManager _states;
    private readonly FantraxApi _fantrax;
    private readonly ISettingsStore _settings;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _time;

    private IReadOnlyList<Game> _games = [];
    private bool _hasStarted;
    private bool _isSyncing;
    private bool _hideBenchPlayers;
    private CancellationTokenSource? _lifetime;

    public AppOrchestrator(
        RosterManager roster,
        ScheduleManager schedule,
        GameMonitor monitor,
        StateManager states,
        FantraxApi fantrax,
        ISettingsStore settings,
        INotificationSink notifications,
        TimeProvider? timeProvider = null)
    {
        _roster = roster;
        _schedule = schedule;
        _monitor = monitor;
        _states = states;
        _fantrax = fantrax;
        _settings = settings;
        _notifications = notifications;
        _time = timeProvider ?? TimeProvider.System;
        _hideBenchPlayers = settings.HideBenchPlayers;

        _monitor.Configure(_states);

        // Swift does this in RosterManager.init; the C# port made it explicit.
        _roster.LoadCachedRoster();
    }

    // MARK: - Published state

    /// <summary>Fired on the Core context whenever any published property changes.</summary>
    public event Action? StateChanged;

    public bool IsSyncing => _isSyncing || _roster.IsSyncing;

    public DateTimeOffset? LastSyncDate => _roster.LastSyncDate;

    public string? SyncError => _roster.Error ?? _schedule.Error;

    public int LoadedPlayerCount => _roster.Players.Count;

    public IReadOnlyList<FantraxTeam> AvailableTeams { get; private set; } = [];

    public bool IsLoadingTeams { get; private set; }

    public string? TeamsError { get; private set; }

    /// <summary>The parsed leagueID from the current URL, if valid.</summary>
    public string? ParsedLeagueId => FantraxUrlParser.Parse(RosterUrl)?.LeagueId;

    /// <summary>Whether the URL already contains a teamId (no picker needed).</summary>
    public bool UrlHasTeamId => FantraxUrlParser.Parse(RosterUrl)?.TeamId is not null;

    /// <summary>The effective teamID — from the URL if available, otherwise from the picker.</summary>
    public string? EffectiveTeamId
    {
        get
        {
            if (FantraxUrlParser.Parse(RosterUrl)?.TeamId is { } teamId) return teamId;
            return string.IsNullOrEmpty(_settings.SelectedTeamId) ? null : _settings.SelectedTeamId;
        }
    }

    private string RosterUrl => _settings.RosterUrl ?? "";

    // MARK: - Lifecycle

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_hasStarted) return;
        _hasStarted = true;

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (RosterUrl.Length == 0) return;
        if (FantraxUrlParser.Parse(RosterUrl) is not { } parsed) return;

        if (parsed.TeamId is { } teamId)
        {
            await _roster.SyncRosterAsync(parsed.LeagueId, teamId, Token);
        }
        else if (_settings.SelectedTeamId is { Length: > 0 } selected)
        {
            await _roster.SyncRosterAsync(parsed.LeagueId, selected, Token);
        }
        else
        {
            // No team selected yet - fetch teams so the user can pick.
            await FetchTeamsAsync();
            return;
        }

        await FetchScheduleAndStartMonitoringAsync();
    }

    // MARK: - Team fetching

    public async Task FetchTeamsAsync()
    {
        if (ParsedLeagueId is not { } leagueId)
        {
            TeamsError = "Invalid Fantrax URL";
            StateChanged?.Invoke();
            return;
        }

        IsLoadingTeams = true;
        TeamsError = null;
        StateChanged?.Invoke();

        try
        {
            AvailableTeams = await _fantrax.FetchTeamsAsync(leagueId, Token);

            // If a team was previously selected and still exists, keep it.
            if (_settings.SelectedTeamId is { Length: > 0 } selected
                && !AvailableTeams.Any(team => team.Id == selected))
            {
                _settings.SelectedTeamId = "";
            }
        }
        catch (Exception ex)
        {
            TeamsError = $"Couldn't load teams: {ex.Message}";
        }

        IsLoadingTeams = false;
        StateChanged?.Invoke();
    }

    /// <summary>Manually triggers a roster re-sync. False on failure (drives the Refresh button).</summary>
    public async Task<bool> ResyncRosterAsync()
    {
        if (ParsedLeagueId is not { } leagueId || EffectiveTeamId is not { } teamId) return false;

        _isSyncing = true;
        StateChanged?.Invoke();

        await _roster.SyncRosterAsync(leagueId, teamId, Token);
        var success = _roster.Error is null;
        await FetchScheduleAndStartMonitoringAsync();

        _isSyncing = false;
        StateChanged?.Invoke();
        return success;
    }

    private async Task FetchScheduleAndStartMonitoringAsync()
    {
        await _notifications.PurgeAllAsync();

        var teamNames = _roster.Players.Select(player => player.Team).ToHashSet(StringComparer.Ordinal);
        await _schedule.FetchScheduleAsync(teamNames, Token);
        _games = _schedule.TodaysGames;

        _states.Reset();
        InitializePlayerStates();

        _monitor.StopMonitoring();
        if (_games.Count == 0) return;

        _monitor.StartMonitoring(_games, _roster.Players);

        // Seed lineup data from the schedule (available before live feed polling starts).
        // StartMonitoring calls StopMonitoring internally, so this must come after it.
        foreach (var game in _games)
        {
            var lineup = new GameLineup
            {
                Home = [.. game.HomeLineup],
                Away = [.. game.AwayLineup],
                HomePitchers = game.HomeProbablePitcherId is { } homePitcher ? [homePitcher] : [],
                AwayPitchers = game.AwayProbablePitcherId is { } awayPitcher ? [awayPitcher] : [],
            };

            if (lineup.IsSubmitted(Game.Side.Home)
                || lineup.IsSubmitted(Game.Side.Away)
                || lineup.HomePitchers.Count > 0
                || lineup.AwayPitchers.Count > 0)
            {
                _monitor.LineupPlayerIds[game.Id] = lineup;
            }
        }
    }

    private void InitializePlayerStates()
    {
        foreach (var game in _games)
        {
            var playerIds = _roster.Players
                .Where(player => IsPlayerInGame(player, game))
                .Select(player => player.Id)
                .ToList();

            _states.SetUpcoming(playerIds, game.StartTime);
        }

        // Mark SP-only players as day off if they're not today's probable pitcher.
        var probablePitcherIds = _games
            .SelectMany(game => new[] { game.HomeProbablePitcherId, game.AwayProbablePitcherId })
            .OfType<int>()
            .ToHashSet();

        foreach (var player in _roster.Players)
        {
            if (!player.IsStartingPitcherOnly || probablePitcherIds.Contains(player.Id)) continue;
            _states.Update(player.Id, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));
        }

        var allGamePlayerIds = _states.PlayerStates.Keys.ToHashSet();
        foreach (var player in _roster.Players)
        {
            if (allGamePlayerIds.Contains(player.Id)) continue;
            _states.Update(player.Id, new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()));
        }
    }

    /// <summary>
    /// Bidirectional substring match so a Fantrax abbreviation ("Dodgers") matches an MLB
    /// full name ("Los Angeles Dodgers") and vice versa.
    /// </summary>
    private static bool IsPlayerInGame(Player player, Game game) =>
        game.HomeTeam.Contains(player.Team, StringComparison.Ordinal)
        || game.AwayTeam.Contains(player.Team, StringComparison.Ordinal)
        || player.Team.Contains(game.HomeTeam, StringComparison.Ordinal)
        || player.Team.Contains(game.AwayTeam, StringComparison.Ordinal);

    private Game? GameFor(Player player) => _games.FirstOrDefault(game => IsPlayerInGame(player, game));

    private CancellationToken Token => _lifetime?.Token ?? CancellationToken.None;
}
