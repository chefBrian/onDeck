using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/ScheduleManager.swift</c>.</summary>
public sealed class ScheduleManager(MlbStatsApi mlb, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<Game> TodaysGames { get; private set; } = [];

    public string? Error { get; private set; }

    /// <summary>
    /// Fetches today's schedule and filters to games involving the given team names.
    /// Uses the "baseball day" — before 8 AM counts as the previous day.
    /// </summary>
    public async Task FetchScheduleAsync(IReadOnlySet<string> teamNames, CancellationToken ct = default)
    {
        Error = null;

        try
        {
            var allGames = await mlb.FetchScheduleAsync(BaseballCalendar.Today(_time), ct);
            TodaysGames =
            [
                .. allGames.Where(game =>
                    teamNames.Contains(game.HomeTeam) || teamNames.Contains(game.AwayTeam))
            ];
        }
        catch (Exception ex)
        {
            Error = $"Schedule fetch failed: {ex.Message}";
        }
    }
}
