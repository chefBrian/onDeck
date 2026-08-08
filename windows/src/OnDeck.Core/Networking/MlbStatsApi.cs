using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;
using OnDeck.Core.Utilities;

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

    // MARK: - Player Search

    public async Task<int?> SearchPlayerAsync(
        string name, string? teamName, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1/people/search?names={Uri.EscapeDataString(name)}&hydrate=currentTeam";
        var response = await GetJsonAsync<SearchResponse>(url, ct);

        if (response.People is not { Count: > 0 } people) return null;

        // If we have a team name for disambiguation, find the matching player.
        if (teamName is not null)
        {
            foreach (var person in people)
            {
                if (person.CurrentTeam?.Name is not { } currentTeamName) continue;

                if (TeamMapping.Matches(currentTeamName, teamName)
                    || currentTeamName.Contains(teamName, StringComparison.Ordinal)
                    || teamName.Contains(currentTeamName, StringComparison.Ordinal))
                {
                    return person.Id;
                }
            }
        }

        // Fall back to first result
        return people[0].Id;
    }

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

    // MARK: - Live Feed

    /// <summary>Fetches the full live feed and returns parsed data.</summary>
    public async Task<LiveFeedData> FetchLiveFeedAsync(int gamePk, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/v1.1/game/{gamePk}/feed/live";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return LiveFeedDecoder.Decode(bytes);
    }

    // MARK: - Diff Patch

    /// <summary>Fetches diff patches for a game since a given timecode.</summary>
    public async Task<DiffPatchResult> FetchDiffPatchAsync(
        int gamePk, string sinceTimecode, CancellationToken ct = default)
    {
        var now = CurrentTimecode();
        var url = $"{BaseUrl}/v1.1/game/{gamePk}/feed/live/diffPatch"
                  + $"?startTimecode={sinceTimecode}&endTimecode={now}";

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        // The API sometimes returns a single feed object (dict) instead of an array. This
        // happens during game phase transitions and resolves itself after a few cycles.
        if (root.ValueKind != JsonValueKind.Array) return new DiffPatchResult.FullUpdate(bytes);

        if (root.GetArrayLength() == 0) return new DiffPatchResult.NoChanges();

        // Entries either carry a "diff" array (patches) or are full feed objects (fallback).
        var operations = new List<PatchOperation>();
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("diff", out var diff)
                && diff.ValueKind == JsonValueKind.Array)
            {
                operations.AddRange(PatchOperation.ParseArray(diff));
                continue;
            }

            // Full feed object instead of patches - hand back just this entry.
            return new DiffPatchResult.FullUpdate(JsonSerializer.SerializeToUtf8Bytes(entry, Options));
        }

        return new DiffPatchResult.Patches(operations);
    }

    /// <summary>
    /// <c>yyyyMMdd_HHmmss</c> UTC — the same format MLB reports in <c>metaData.timeStamp</c>,
    /// which is where the caller's <c>startTimecode</c> comes from.
    /// </summary>
    private string CurrentTimecode() =>
        timeProvider.GetUtcNow().ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    // MARK: - Game Changes

    /// <summary>Returns the set of gamePks that have been updated since <paramref name="since"/>.</summary>
    public async Task<IReadOnlySet<int>> FetchGameChangesAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        var timestamp = since.ToUniversalTime()
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl}/v1/game/changes?updatedSince={Uri.EscapeDataString(timestamp)}&sportId=1";

        var response = await GetJsonAsync<GameChangesResponse>(url, ct);

        var gamePks = new HashSet<int>();
        foreach (var date in response.Dates ?? [])
        {
            foreach (var game in date.Games ?? []) gamePks.Add(game.GamePk);
        }

        return gamePks;
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct)
               ?? throw new JsonException($"{url} decoded to null");
    }

    // --- Player search DTOs

    private sealed class SearchResponse
    {
        public List<SearchPerson>? People { get; set; }
    }

    private sealed class SearchPerson
    {
        public int Id { get; set; }
        public required string FullName { get; set; }
        public SearchTeam? CurrentTeam { get; set; }
    }

    private sealed class SearchTeam
    {
        public int Id { get; set; }
        public required string Name { get; set; }
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

    // --- Game changes DTOs

    private sealed class GameChangesResponse
    {
        public List<GameChangesDate>? Dates { get; set; }
    }

    private sealed class GameChangesDate
    {
        public List<GameChangesGame>? Games { get; set; }
    }

    private sealed class GameChangesGame
    {
        public int GamePk { get; set; }
    }
}
