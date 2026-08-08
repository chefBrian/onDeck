using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>
/// Port of <c>Managers/GameMonitor.swift</c>. Centralised polling coordinator: one loop
/// sleeps to the next event (pre-game milestone or the 15-min-before-start active window),
/// then polls every game in range at 10s using diffPatch, falling back to a full feed fetch
/// on error or transition.
/// </summary>
public sealed class GameMonitor(MlbStatsApi mlb, TimeProvider? timeProvider = null)
{
    /// <summary>Pre-game milestone times (before game start) for one-shot lineup checks.</summary>
    private static readonly TimeSpan[] PreGameMilestones =
        [TimeSpan.FromHours(2), TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)];

    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private CancellationTokenSource? _coordinator;
    private Task? _coordinatorTask;
    private readonly Dictionary<int, Game> _monitoredGames = [];
    private HashSet<int> _rosterPlayerIds = [];
    private Dictionary<int, Player> _rosterPlayers = [];
    private StateManager? _stateManager;

    // Tracks previously seen batter/pitcher per game to detect transitions.
    private readonly Dictionary<int, int?> _lastBatterId = [];
    private readonly Dictionary<int, int?> _lastPitcherId = [];
    private readonly Dictionary<int, int> _lastHomePitcherId = [];
    private readonly Dictionary<int, int> _lastAwayPitcherId = [];

    /// <summary>Tracks which pre-game milestones have been fetched per game.</summary>
    private readonly Dictionary<int, HashSet<TimeSpan>> _completedMilestones = [];

    /// <summary>Games observed Live/In Progress at least once (for one-shot start detection).</summary>
    private readonly HashSet<int> _liveGamesSeen = [];

    public bool IsMonitoring { get; private set; }

    /// <summary>Last completed play description per player (for result notifications).</summary>
    public Dictionary<int, string> LastPlayDescriptions { get; } = [];

    /// <summary>Latest feed data per game (for In Game player display).</summary>
    public Dictionary<int, LiveFeedData> LatestFeeds { get; } = [];

    /// <summary>
    /// Lineup player IDs per game, tracked per side so consumers can tell whether a player's
    /// own team has submitted yet (vs just the opponent).
    /// </summary>
    public Dictionary<int, GameLineup> LineupPlayerIds { get; } = [];

    /// <summary>Fired when <see cref="LineupPlayerIds"/> for a game is populated or changes.</summary>
    public Action<int>? OnLineupUpdate { get; set; }

    /// <summary>Fired once per game the first time it transitions to Live/In Progress.</summary>
    public Action<int>? OnGameStart { get; set; }

    /// <summary>
    /// Whether the feed has observed this game as Live/In Progress. Driven by the feed, not
    /// the clock, so late-starting games aren't misclassified.
    /// </summary>
    public bool IsLive(int gamePk) => _liveGamesSeen.Contains(gamePk);

    public void Configure(StateManager stateManager) => _stateManager = stateManager;

    /// <summary>
    /// Resets all state and starts the coordinator. Callers seeding data (e.g.
    /// <see cref="LineupPlayerIds"/>) must do so <em>after</em> this returns — it calls
    /// <see cref="StopMonitoring()"/> internally, which clears everything.
    /// </summary>
    public void StartMonitoring(IReadOnlyList<Game> games, IReadOnlyList<Player> players)
    {
        TrackGames(games, players);

        _coordinator = new CancellationTokenSource();
        _coordinatorTask = CoordinatePollingAsync(_coordinator.Token);
    }

    /// <summary>
    /// Resets tracking state for a new set of games without launching the coordinator.
    /// <see cref="StartMonitoring"/> is this plus the polling loop; tests use it directly to
    /// exercise <see cref="NextEventDelay"/> and <see cref="SelectGamesToPoll"/> without the
    /// loop concurrently consuming milestones.
    /// </summary>
    internal void TrackGames(IReadOnlyList<Game> games, IReadOnlyList<Player> players)
    {
        StopMonitoring();

        _rosterPlayerIds = [.. players.Select(p => p.Id)];
        _rosterPlayers = players.ToDictionary(p => p.Id);
        foreach (var game in games) _monitoredGames[game.Id] = game;
        IsMonitoring = true;
    }

    public void StopMonitoring()
    {
        CancelCoordinator();

        _monitoredGames.Clear();
        _lastBatterId.Clear();
        _lastPitcherId.Clear();
        _lastHomePitcherId.Clear();
        _lastAwayPitcherId.Clear();
        LineupPlayerIds.Clear();
        _liveGamesSeen.Clear();
        _completedMilestones.Clear();

        // A full stop (e.g. midnight refresh) drops LatestFeeds. The per-game overload
        // intentionally retains them so the Done section can keep reading finished games.
        LatestFeeds.Clear();
        LastPlayDescriptions.Clear();
        IsMonitoring = false;
    }

    /// <summary>Stops monitoring a specific game (e.g. when no roster players remain).</summary>
    public void StopMonitoring(int gamePk)
    {
        _monitoredGames.Remove(gamePk);
        LineupPlayerIds.Remove(gamePk);
        _lastBatterId.Remove(gamePk);
        _lastPitcherId.Remove(gamePk);
        _lastHomePitcherId.Remove(gamePk);
        _lastAwayPitcherId.Remove(gamePk);
        _completedMilestones.Remove(gamePk);
        _liveGamesSeen.Remove(gamePk);

        // Keep LatestFeeds[gamePk] - the Done section reads feed.PlayerStats for finished games.
        if (_monitoredGames.Count != 0) return;

        CancelCoordinator();
        IsMonitoring = false;
    }

    /// <summary>
    /// Nulls each cached feed's timestamp so the next poll cycle does a full fetch per game.
    /// Used after system wake when stored timecodes are stale. Preserves the rest of each
    /// feed so the UI keeps rendering last-known state during the round trip.
    /// </summary>
    public void InvalidateTimecodes()
    {
        foreach (var feed in LatestFeeds.Values) feed.TimeStamp = null;
    }

    private void CancelCoordinator()
    {
        _coordinator?.Cancel();
        _coordinator?.Dispose();
        _coordinator = null;
        _coordinatorTask = null;
    }

    // MARK: - Centralized Polling

    private async Task CoordinatePollingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sleepDuration = NextEventDelay();
            if (sleepDuration > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(sleepDuration, _time, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            await PollCycleAsync(ct);

            // Once any game is in active polling range, switch to the 10s loop.
            var hasActiveGames = _monitoredGames.Values.Any(
                game => game.StartTime - ActiveWindow <= _time.GetUtcNow());

            if (!hasActiveGames) continue;

            try
            {
                await Task.Delay(PollInterval, _time, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollCycleAsync(CancellationToken ct)
    {
        var gamesToPoll = SelectGamesToPoll();
        if (gamesToPoll.Count == 0) return;

        await Task.WhenAll(gamesToPoll
            .Where(_monitoredGames.ContainsKey)
            .Select(gamePk => PollSingleGameAsync(gamePk, _monitoredGames[gamePk], ct)));
    }

    /// <summary>
    /// Games due for a poll this cycle: everything inside the active window, plus any game
    /// that has just crossed an uncompleted pre-game milestone. One cycle consumes at most
    /// one milestone per game.
    /// </summary>
    internal IReadOnlyList<int> SelectGamesToPoll()
    {
        var now = _time.GetUtcNow();
        var gamesToPoll = new List<int>();

        // Active games: within 15 min of start - poll every cycle.
        foreach (var (gamePk, game) in _monitoredGames)
        {
            if (game.StartTime - ActiveWindow <= now) gamesToPoll.Add(gamePk);
        }

        // Pre-game milestone checks: one-shot fetch when a milestone is reached.
        foreach (var (gamePk, game) in _monitoredGames)
        {
            var timeUntilStart = game.StartTime - now;
            if (timeUntilStart <= ActiveWindow) continue;   // already active

            foreach (var milestone in PreGameMilestones)
            {
                if (timeUntilStart > milestone) continue;

                if (!_completedMilestones.TryGetValue(gamePk, out var completed))
                {
                    completed = [];
                    _completedMilestones[gamePk] = completed;
                }

                if (!completed.Add(milestone)) continue;

                gamesToPoll.Add(gamePk);
                break;
            }
        }

        return gamesToPoll;
    }

    /// <summary>
    /// Time until the next event (milestone or active polling window).
    /// <see cref="TimeSpan.Zero"/> when an event is ready now.
    /// </summary>
    internal TimeSpan NextEventDelay()
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? nextTime = null;

        foreach (var game in _monitoredGames.Values)
        {
            // Active polling starts 15 min before the game.
            var activeStart = game.StartTime - ActiveWindow;
            if (activeStart <= now) return TimeSpan.Zero;

            _completedMilestones.TryGetValue(game.Id, out var completed);

            foreach (var milestone in PreGameMilestones)
            {
                var milestoneTime = game.StartTime - milestone;
                if (milestoneTime <= now && !(completed?.Contains(milestone) ?? false)) return TimeSpan.Zero;
                if (milestoneTime > now && (nextTime is null || milestoneTime < nextTime)) nextTime = milestoneTime;
            }

            if (nextTime is null || activeStart < nextTime) nextTime = activeStart;
        }

        if (nextTime is not { } next) return TimeSpan.Zero;

        var delay = next - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    internal async Task PollSingleGameAsync(int gamePk, Game game, CancellationToken ct)
    {
        try
        {
            LiveFeedData feed;

            if (LatestFeeds.TryGetValue(gamePk, out var existing) && existing.TimeStamp is { } timecode)
            {
                var result = await mlb.FetchDiffPatchAsync(gamePk, timecode, ct);

                switch (result)
                {
                    case DiffPatchResult.NoChanges:
                        return;

                    case DiffPatchResult.Patches patches:
                        feed = LiveFeedPatcher.Apply(patches.Operations, existing);
                        LatestFeeds[gamePk] = feed;
                        break;

                    case DiffPatchResult.FullUpdate full:
                        feed = LiveFeedDecoder.Decode(full.Json);
                        LatestFeeds[gamePk] = feed;
                        break;

                    default:
                        return;
                }
            }
            else
            {
                // No seed - full fetch.
                feed = await mlb.FetchLiveFeedAsync(gamePk, ct);
                LatestFeeds[gamePk] = feed;
            }

            ProcessFeed(feed, gamePk, game);

            if (feed.GameState != "Final") return;

            // Postponed carries gameState "Final" but has no stats - marking players gameOver
            // would filter them out of the UI entirely (the Done section requires a stat
            // line). Leave them in .upcoming so the UPCOMING row's red X icon and "PPD" label
            // stay visible until the next day's refresh.
            if (feed.DetailedState == "Postponed")
            {
                StopMonitoring(gamePk);
                return;
            }

            var playerIdsInGame = _rosterPlayerIds.Where(id => IsPlayerInGame(id, game)).ToArray();
            _stateManager?.SetGameOver(playerIdsInGame, gamePk);
            StopMonitoring(gamePk);
        }
        catch (Exception)
        {
            // Transient error - preserve the last-known feed for UI continuity, but null its
            // timestamp so the next cycle does a full fetch.
            if (LatestFeeds.TryGetValue(gamePk, out var stale)) stale.TimeStamp = null;
        }
    }

    // MARK: - Helpers

    private bool IsPlayerInGame(int playerId, Game game)
    {
        if (!_rosterPlayers.TryGetValue(playerId, out var player)) return false;

        return game.HomeTeam.Contains(player.Team, StringComparison.Ordinal)
            || game.AwayTeam.Contains(player.Team, StringComparison.Ordinal)
            || player.Team.Contains(game.HomeTeam, StringComparison.Ordinal)
            || player.Team.Contains(game.AwayTeam, StringComparison.Ordinal);
    }

    // Task 8 adds ProcessFeed.
    private void ProcessFeed(LiveFeedData feed, int gamePk, Game game) { }
}
