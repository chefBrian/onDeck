using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;

namespace OnDeck.Core.Networking;

/// <summary>Port of <c>Networking/MLBStatsAPI.swift</c>.</summary>
public sealed class MlbStatsApi(HttpClient http, TimeProvider timeProvider)
{
    private const string BaseUrl = "https://statsapi.mlb.com/api";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public MlbStatsApi(HttpClient http) : this(http, TimeProvider.System) { }

    /// <summary>
    /// Swift pins <c>urlCache = nil</c> and <c>httpMaximumConnectionsPerHost = 2</c> to keep
    /// poll-cycle residency down. .NET does not cache responses by default, so only the
    /// connection cap carries over.
    /// </summary>
    public static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = 2 });

    // MARK: - Schedule

    public async Task<IReadOnlyList<Game>> FetchScheduleAsync(
        DateTimeOffset date, CancellationToken ct = default)
    {
        var dateString = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/v1/schedule?sportId=1&date={dateString}"
                  + "&hydrate=team,broadcasts,probablePitcher,lineups";

        var response = await GetJsonAsync<ScheduleResponse>(url, ct);

        var games = new List<Game>();
        foreach (var scheduleDate in response.Dates ?? [])
        {
            foreach (var game in scheduleDate.Games ?? []) games.Add(MapGame(game));
        }

        return games;
    }

    private Game MapGame(ScheduleGame game)
    {
        var broadcasts = new List<Game.Broadcast>();
        foreach (var broadcast in game.Broadcasts ?? [])
        {
            if (broadcast.CallSign is not { } callSign) continue;
            broadcasts.Add(new Game.Broadcast(
                callSign,
                broadcast.Availability?.AvailabilityCode == "exclusive"));
        }

        var startTime = DateTimeOffset.TryParse(
            game.GameDate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : timeProvider.GetUtcNow();

        return new Game(
            game.GamePk,
            game.Teams.Home.Team.Name,
            game.Teams.Away.Team.Name,
            game.Teams.Home.Team.Id,
            game.Teams.Away.Team.Id,
            startTime,
            game.Teams.Home.ProbablePitcher?.Id,
            game.Teams.Away.ProbablePitcher?.Id,
            broadcasts,
            [.. (game.Lineups?.HomePlayers ?? []).Select(p => p.Id)],
            [.. (game.Lineups?.AwayPlayers ?? []).Select(p => p.Id)]);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct)
               ?? throw new JsonException($"{url} decoded to null");
    }

    // --- Schedule DTOs

    private sealed class ScheduleResponse
    {
        public List<ScheduleDate>? Dates { get; set; }
    }

    private sealed class ScheduleDate
    {
        public List<ScheduleGame>? Games { get; set; }
    }

    private sealed class ScheduleGame
    {
        public int GamePk { get; set; }
        public required string GameDate { get; set; }
        public required ScheduleGameTeams Teams { get; set; }
        public List<ScheduleBroadcast>? Broadcasts { get; set; }
        public ScheduleLineups? Lineups { get; set; }
    }

    private sealed class ScheduleGameTeams
    {
        public required ScheduleTeamEntry Away { get; set; }
        public required ScheduleTeamEntry Home { get; set; }
    }

    private sealed class ScheduleTeamEntry
    {
        public required ScheduleTeamInfo Team { get; set; }
        public ScheduleProbablePitcher? ProbablePitcher { get; set; }
    }

    private sealed class ScheduleTeamInfo
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }

    private sealed class ScheduleProbablePitcher
    {
        public int Id { get; set; }
    }

    private sealed class ScheduleBroadcast
    {
        public string? Type { get; set; }
        public string? CallSign { get; set; }
        public BroadcastAvailability? Availability { get; set; }
    }

    private sealed class BroadcastAvailability
    {
        public string? AvailabilityCode { get; set; }
    }

    private sealed class ScheduleLineups
    {
        public List<ScheduleLineupPlayer>? HomePlayers { get; set; }
        public List<ScheduleLineupPlayer>? AwayPlayers { get; set; }
    }

    private sealed class ScheduleLineupPlayer
    {
        public int Id { get; set; }
    }
}
