using OnDeck.Core.Models;
using OnDeck.Core.Networking;

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
        StopMonitoring();

        _rosterPlayerIds = [.. players.Select(p => p.Id)];
        _rosterPlayers = players.ToDictionary(p => p.Id);
        foreach (var game in games) _monitoredGames[game.Id] = game;
        IsMonitoring = true;

        _coordinator = new CancellationTokenSource();
        _coordinatorTask = CoordinatePollingAsync(_coordinator.Token);
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

    // Task 6 adds CoordinatePollingAsync, NextEventDelay and SelectGamesToPoll.
    private Task CoordinatePollingAsync(CancellationToken ct) => Task.CompletedTask;
}
