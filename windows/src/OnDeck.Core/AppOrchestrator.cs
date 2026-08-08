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
    private readonly SynchronizationContext? _context;

    private IReadOnlyList<Game> _games = [];
    private bool _hasStarted;
    private bool _isSyncing;
    private bool _hideBenchPlayers;
    private bool _playerListsDirty;
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

        _context = SynchronizationContext.Current;

        _monitor.Configure(_states);
        _states.OnStateChange = HandleStateChange;
        _monitor.OnGameStart = HandleGameStart;

        // Swift does this in RosterManager.init; the C# port made it explicit.
        _roster.LoadCachedRoster();
    }

    // MARK: - Published state

    /// <summary>Fired on the Core context whenever any published property changes.</summary>
    public event Action? StateChanged;

    /// <summary>At bat or on the mound right now, in roster order.</summary>
    public IReadOnlyList<PlayerDisplay> ActivePlayers { get; private set; } = [];

    /// <summary>Game started, not currently active — pre-sorted per the MenuBarView rules.</summary>
    public IReadOnlyList<PlayerDisplay> InGamePlayers { get; private set; } = [];

    /// <summary>Game hasn't started, sorted by first pitch then name.</summary>
    public IReadOnlyList<PlayerDisplay> UpcomingPlayers { get; private set; } = [];

    /// <summary>Game over or substituted out, filtered to players with a matching stat line.</summary>
    public IReadOnlyList<PlayerDisplay> DonePlayers { get; private set; } = [];

    /// <summary>Drives the green tray icon.</summary>
    public bool HasActivePlayers => ActivePlayers.Count > 0;

    /// <summary>"A | B | C +2" — the tray tooltip.</summary>
    public string MenuBarTitleText
    {
        get
        {
            var names = ActivePlayers.Select(display => display.Name).ToList();
            return names.Count switch
            {
                0 => "",
                <= 3 => string.Join(" | ", names),
                _ => string.Join(" | ", names.Take(3)) + $" +{names.Count - 3}",
            };
        }
    }

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

        // InitializePlayerStates built the lists before the lineups above existed. On macOS the
        // @Observable views just re-read GameMonitor at render time; here the rows are snapshots,
        // so rebuild now that the badges can be resolved.
        UpdatePlayerLists();
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

        UpdatePlayerLists();
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

    /// <summary>
    /// Re-reads <see cref="ISettingsStore"/> and rebuilds the lists locally. The
    /// <c>hideBenchPlayers</c> didSet analog — never touches the network.
    /// </summary>
    public void SettingsChanged()
    {
        _hideBenchPlayers = _settings.HideBenchPlayers;
        UpdatePlayerLists();
    }

    // MARK: - List building

    private void UpdatePlayerLists()
    {
        var active = new List<PlayerDisplay>();
        var inGame = new List<PlayerDisplay>();
        var upcoming = new List<PlayerDisplay>();
        var done = new List<PlayerDisplay>();

        foreach (var player in _roster.Players)
        {
            if (player.IsUnavailable) continue;
            if (_hideBenchPlayers && player.IsOnBench) continue;

            switch (_states.PlayerStates.GetValueOrDefault(player.Id))
            {
                case PlayerState.Active:
                    active.Add(BuildLiveRow(player, isActive: true));
                    break;

                case PlayerState.Upcoming upcomingState:
                    if (GameFor(player) is { } game && _monitor.IsLive(game.Id))
                    {
                        inGame.Add(BuildLiveRow(player, isActive: false));
                    }
                    else
                    {
                        upcoming.Add(BuildUpcomingRow(player, upcomingState.StartTime));
                    }

                    break;

                case PlayerState.Inactive { Reason: PlayerState.InactiveReason.GameOver over }:
                    AddDoneRow(done, player, over.GamePk);
                    break;

                case PlayerState.Inactive { Reason: PlayerState.InactiveReason.Substituted substituted }:
                    AddDoneRow(done, player, substituted.GamePk);
                    break;
            }
        }

        ActivePlayers = active;
        InGamePlayers = [.. inGame.OrderBy(display => display.SortKey)];
        UpcomingPlayers =
        [
            .. upcoming
                .OrderBy(display => display.StartTime ?? DateTimeOffset.MaxValue)
                .ThenBy(display => display.Name, StringComparer.Ordinal)
        ];
        DonePlayers = [.. done.OrderBy(display => display.Player.IsHitter ? 0 : 1)];

        StateChanged?.Invoke();
    }

    private void AddDoneRow(List<PlayerDisplay> done, Player player, int gamePk)
    {
        var feed = _monitor.LatestFeeds.GetValueOrDefault(gamePk);
        if (DisplayRules.RawStatLine(player, feed) is not { } statLine) return;

        done.Add(new PlayerDisplay
        {
            Player = player,
            GamePk = gamePk,
            Feed = feed,
            StatLine = statLine,
        });
    }

    private PlayerDisplay BuildLiveRow(Player player, bool isActive)
    {
        var game = GameFor(player);
        var feed = game is null ? null : _monitor.LatestFeeds.GetValueOrDefault(game.Id);
        var lineup = game is null ? null : _monitor.LineupPlayerIds.GetValueOrDefault(game.Id);
        var proximity = DisplayRules.ProximityFor(player, feed);
        var isInLineup = DisplayRules.IsInLineup(player, game, lineup);

        return new PlayerDisplay
        {
            Player = player,
            GamePk = game?.Id,
            Feed = feed,
            IsActive = isActive,
            Proximity = proximity,
            IsInLineup = isInLineup,
            StatLine = game is null ? null : DisplayRules.LiveStatLine(player, feed, isInLineup, proximity),
            Delay = DisplayRules.DelayFor(feed?.DetailedState),
            StreamUrl = game is null ? null : StreamLinkRouter.Url(game),
            SortKey = DisplayRules.InGameSortKey(player, game, feed, lineup, proximity),
        };
    }

    // MARK: - Change handling

    private void HandleStateChange(int playerId, PlayerState? oldState, PlayerState newState) =>
        SchedulePlayerListRebuild();

    private void HandleGameStart(int gamePk)
    {
        // The feed just flipped this game to In Progress - rebuild so upcoming players on it
        // move to the in-game bucket.
        SchedulePlayerListRebuild();
        RunGuarded(() => _notifications.PurgeNotInLineupAsync(gamePk));
    }

    /// <summary>
    /// Coalesces list rebuilds. A single poll cycle can fire 10+ state updates (pitcher
    /// substitution sweep, batter and pitcher transitions); a full roster scan for each is
    /// wasteful. Defer to the next tick on the Core context so all updates in one synchronous
    /// pass collapse into one rebuild.
    /// </summary>
    private void SchedulePlayerListRebuild()
    {
        if (_playerListsDirty) return;
        _playerListsDirty = true;

        Post(() =>
        {
            _playerListsDirty = false;
            UpdatePlayerLists();
        });
    }

    /// <summary>
    /// Queues work on the Core context — Swift's <c>Task { @MainActor in ... }</c>. Falls back
    /// to a yielded continuation only when no context is installed (never true in the app or
    /// in tests).
    /// </summary>
    private void Post(Action action)
    {
        if (_context is not null)
        {
            _context.Post(static state => ((Action)state!)(), action);
            return;
        }

        _ = YieldThen(action);

        static async Task YieldThen(Action queued)
        {
            await Task.Yield();
            queued();
        }
    }

    /// <summary>
    /// Fire-and-forget on the Core context. The sink is shell-implemented (the toast API can
    /// throw); a failed notification must not tear down the transition pipeline.
    /// </summary>
    private void RunGuarded(Func<Task> work) => _ = RunGuardedAsync(work);

    private static async Task RunGuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppOrchestrator] notification work failed: {ex}");
        }
    }

    private PlayerDisplay BuildUpcomingRow(Player player, DateTimeOffset startTime)
    {
        var game = GameFor(player);
        var feed = game is null ? null : _monitor.LatestFeeds.GetValueOrDefault(game.Id);
        var lineup = game is null ? null : _monitor.LineupPlayerIds.GetValueOrDefault(game.Id);

        return new PlayerDisplay
        {
            Player = player,
            GamePk = game?.Id,
            Feed = feed,
            Lineup = DisplayRules.LineupInfoFor(player, game, lineup, feed),
            Delay = DisplayRules.DelayFor(feed?.DetailedState),
            StartTime = startTime,
            StreamUrl = game is null ? null : StreamLinkRouter.Url(game),
        };
    }
}
