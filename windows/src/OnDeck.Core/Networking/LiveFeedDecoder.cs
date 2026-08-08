using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;

namespace OnDeck.Core.Networking;

/// <summary>
/// Decodes MLB <c>/feed/live</c> JSON into <see cref="LiveFeedData"/>. Port of
/// <c>MLBStatsAPI.decodeLiveFeed</c> + <c>parseLiveFeedResponse</c> + <c>parsePlayerStats</c>.
/// </summary>
public static class LiveFeedDecoder
{
    // The wire format is camelCase (timeStamp, abstractGameState, battingOrder) and the DTO
    // properties are PascalCase, so case-insensitive matching is required.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static LiveFeedData Decode(string json) =>
        Parse(JsonSerializer.Deserialize<FeedResponse>(json, Options)
              ?? throw new JsonException("feed/live payload decoded to null"));

    public static LiveFeedData Decode(ReadOnlySpan<byte> utf8Json) =>
        Parse(JsonSerializer.Deserialize<FeedResponse>(utf8Json, Options)
              ?? throw new JsonException("feed/live payload decoded to null"));

    private static LiveFeedData Parse(FeedResponse response)
    {
        var currentPlay = response.LiveData?.Plays?.CurrentPlay;
        var linescore = response.LiveData?.Linescore;
        var offense = linescore?.Offense;
        var boxscore = response.LiveData?.Boxscore;

        return new LiveFeedData
        {
            TimeStamp = response.MetaData?.TimeStamp,
            GameState = response.GameData.Status.AbstractGameState,
            DetailedState = response.GameData.Status.DetailedState,
            CurrentBatterId = currentPlay?.Matchup?.Batter?.Id,
            CurrentBatterName = currentPlay?.Matchup?.Batter?.FullName,
            CurrentPitcherId = currentPlay?.Matchup?.Pitcher?.Id,
            CurrentPitcherName = currentPlay?.Matchup?.Pitcher?.FullName,
            Inning = linescore?.CurrentInning,
            InningHalf = linescore?.InningHalf,
            InningState = linescore?.InningState,
            HomeScore = linescore?.Teams?.Home?.Runs ?? 0,
            AwayScore = linescore?.Teams?.Away?.Runs ?? 0,
            HomeTeam = response.GameData.Teams.Home.Name,
            AwayTeam = response.GameData.Teams.Away.Name,
            HomeTeamId = response.GameData.Teams.Home.Id,
            AwayTeamId = response.GameData.Teams.Away.Id,
            Balls = currentPlay?.Count?.Balls ?? 0,
            Strikes = currentPlay?.Count?.Strikes ?? 0,
            Outs = currentPlay?.Count?.Outs ?? 0,
            RunnerOnFirst = offense?.First?.Id,
            RunnerOnSecond = offense?.Second?.Id,
            RunnerOnThird = offense?.Third?.Id,
            IsPlayComplete = currentPlay?.About?.IsComplete ?? false,
            LastPlayEvent = currentPlay?.Result?.Event,
            LastPlayDescription = currentPlay?.Result?.Description,
            HomeBattingOrder = [.. boxscore?.Teams?.Home?.BattingOrder ?? []],
            AwayBattingOrder = [.. boxscore?.Teams?.Away?.BattingOrder ?? []],
            HomePitchers = [.. boxscore?.Teams?.Home?.Pitchers ?? []],
            AwayPitchers = [.. boxscore?.Teams?.Away?.Pitchers ?? []],
            PlayerStats = ParsePlayerStats(boxscore),
        };
    }

    private static Dictionary<int, PlayerGameStats> ParsePlayerStats(FeedBoxscore? boxscore)
    {
        var result = new Dictionary<int, PlayerGameStats>();
        if (boxscore?.Teams is not { } teams) return result;

        foreach (var team in new[] { teams.Home, teams.Away })
        {
            if (team?.Players is not { } players) continue;

            foreach (var (key, player) in players)
            {
                if (!key.StartsWith("ID", StringComparison.Ordinal)) continue;
                if (!int.TryParse(key.AsSpan(2), out var id)) continue;
                if (player.Stats is not { } stats) continue;
                if (stats.Batting is null && stats.Pitching is null) continue;

                result[id] = new PlayerGameStats { Batting = stats.Batting, Pitching = stats.Pitching };
            }
        }

        return result;
    }

    // --- DTOs mirroring the private Codable types in MLBStatsAPI.swift.

    private sealed class FeedResponse
    {
        public FeedMetaData? MetaData { get; set; }
        public required FeedGameData GameData { get; set; }
        public FeedLiveData? LiveData { get; set; }
    }

    private sealed class FeedMetaData
    {
        public string? TimeStamp { get; set; }
    }

    private sealed class FeedGameData
    {
        public required FeedGameStatus Status { get; set; }
        public required FeedGameTeams Teams { get; set; }
    }

    private sealed class FeedGameStatus
    {
        public required string AbstractGameState { get; set; }
        public string? DetailedState { get; set; }
    }

    private sealed class FeedGameTeams
    {
        public required FeedTeamEntry Away { get; set; }
        public required FeedTeamEntry Home { get; set; }
    }

    private sealed class FeedTeamEntry
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    private sealed class FeedLiveData
    {
        public FeedPlays? Plays { get; set; }
        public FeedLinescore? Linescore { get; set; }
        public FeedBoxscore? Boxscore { get; set; }
    }

    private sealed class FeedPlays
    {
        public FeedCurrentPlay? CurrentPlay { get; set; }
    }

    private sealed class FeedCurrentPlay
    {
        public FeedPlayResult? Result { get; set; }
        public FeedPlayAbout? About { get; set; }
        public FeedMatchup? Matchup { get; set; }
        public FeedPlayCount? Count { get; set; }
    }

    private sealed class FeedPlayResult
    {
        public string? Type { get; set; }
        public string? Event { get; set; }
        public string? Description { get; set; }
    }

    private sealed class FeedPlayAbout
    {
        public bool IsComplete { get; set; }
    }

    private sealed class FeedMatchup
    {
        public FeedPlayer? Batter { get; set; }
        public FeedPlayer? Pitcher { get; set; }
    }

    private sealed class FeedPlayer
    {
        public int Id { get; set; }
        public string? FullName { get; set; }
    }

    private sealed class FeedPlayCount
    {
        public int Balls { get; set; }
        public int Strikes { get; set; }
        public int Outs { get; set; }
    }

    private sealed class FeedLinescore
    {
        public int? CurrentInning { get; set; }
        public string? InningHalf { get; set; }
        public string? InningState { get; set; }
        public FeedLinescoreTeams? Teams { get; set; }
        public FeedOffense? Offense { get; set; }
    }

    private sealed class FeedOffense
    {
        public FeedRunner? First { get; set; }
        public FeedRunner? Second { get; set; }
        public FeedRunner? Third { get; set; }
    }

    private sealed class FeedRunner
    {
        public int? Id { get; set; }
        public string? FullName { get; set; }
    }

    private sealed class FeedLinescoreTeams
    {
        public FeedLinescoreTeam? Home { get; set; }
        public FeedLinescoreTeam? Away { get; set; }
    }

    private sealed class FeedLinescoreTeam
    {
        public int? Runs { get; set; }
    }

    private sealed class FeedBoxscore
    {
        public FeedBoxscoreTeams? Teams { get; set; }
    }

    private sealed class FeedBoxscoreTeams
    {
        public FeedBoxscoreTeamEntry? Away { get; set; }
        public FeedBoxscoreTeamEntry? Home { get; set; }
    }

    private sealed class FeedBoxscoreTeamEntry
    {
        public List<int>? BattingOrder { get; set; }
        public List<int>? Pitchers { get; set; }
        public Dictionary<string, FeedBoxscorePlayer>? Players { get; set; }
    }

    private sealed class FeedBoxscorePlayer
    {
        public FeedBoxscorePlayerStats? Stats { get; set; }
    }

    private sealed class FeedBoxscorePlayerStats
    {
        public PlayerBattingStats? Batting { get; set; }
        public PlayerPitchingStats? Pitching { get; set; }
    }
}
