using OnDeck.Core.Models;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/StateManager.swift</c>.</summary>
public sealed class StateManager
{
    /// <summary>Keyed by MLB player ID.</summary>
    public Dictionary<int, PlayerState> PlayerStates { get; } = [];

    /// <summary>Fired when a player's state changes. Args: (playerId, oldState, newState).</summary>
    public Action<int, PlayerState?, PlayerState>? OnStateChange { get; set; }

    public void Update(int playerId, PlayerState state)
    {
        PlayerStates.TryGetValue(playerId, out var oldState);
        PlayerStates[playerId] = state;
        OnStateChange?.Invoke(playerId, oldState, state);
    }

    public DateTimeOffset? StartTimeFor(int playerId) =>
        PlayerStates.TryGetValue(playerId, out var state) && state is PlayerState.Upcoming upcoming
            ? upcoming.StartTime
            : null;

    /// <summary>Sets players to upcoming with a given start time (used when the schedule is fetched).</summary>
    public void SetUpcoming(IReadOnlyList<int> playerIds, DateTimeOffset startTime)
    {
        foreach (var id in playerIds)
        {
            if (!PlayerStates.ContainsKey(id)) PlayerStates[id] = new PlayerState.Upcoming(startTime);
        }
    }

    /// <summary>Sets all players in a game to inactive (game over).</summary>
    public void SetGameOver(IReadOnlyList<int> playerIds, int gamePk)
    {
        foreach (var id in playerIds)
        {
            Update(id, new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(gamePk)));
        }
    }

    /// <summary>Clears all state (e.g. on a new day).</summary>
    public void Reset() => PlayerStates.Clear();
}
