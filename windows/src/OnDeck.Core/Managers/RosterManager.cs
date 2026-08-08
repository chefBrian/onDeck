using System.Text.Json;
using System.Text.Json.Serialization;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Managers;

/// <summary>Port of <c>Managers/RosterManager.swift</c>.</summary>
public sealed class RosterManager(
    FantraxApi fantrax,
    MlbStatsApi mlb,
    ISettingsStore settings,
    HeadshotCache? headshots = null,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions CacheOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<Player> Players { get; private set; } = [];

    public DateTimeOffset? LastSyncDate { get; private set; }

    public string? Error { get; private set; }

    public bool IsSyncing { get; private set; }

    public async Task SyncRosterAsync(string leagueId, string teamId, CancellationToken ct = default)
    {
        IsSyncing = true;
        Error = null;

        try
        {
            var fantraxPlayers = await fantrax.FetchRosterAsync(leagueId, teamId, ct);

            // Resolve all MLB IDs concurrently.
            var resolved = await Task.WhenAll(fantraxPlayers.Select(async fp =>
            {
                try
                {
                    var id = await mlb.SearchPlayerAsync(NameCleaner.Clean(fp.Name), fp.TeamShortName, ct);
                    return id is { } mlbId ? (Player: fp, MlbId: mlbId) : ((FantraxPlayer Player, int MlbId)?)null;
                }
                catch (Exception)
                {
                    return null;
                }
            }));

            var byMlbId = new Dictionary<int, Player>();   // keyed by MLB ID for dedup

            foreach (var entry in resolved)
            {
                if (entry is not { } resolvedEntry) continue;
                var (fp, mlbId) = resolvedEntry;

                var positions = ParsePositions(fp.Positions);
                var rawPositions = fp.Positions
                    .Select(p => p.Trim().ToUpperInvariant())
                    .ToHashSet(StringComparer.Ordinal);
                var rosterStatus = Enum.IsDefined(typeof(RosterStatus), fp.StatusId)
                    ? (RosterStatus)fp.StatusId
                    : RosterStatus.Active;

                if (byMlbId.TryGetValue(mlbId, out var existing))
                {
                    // Merge positions for two-way players (e.g. Ohtani).
                    var merged = existing.Positions.ToHashSet();
                    merged.UnionWith(positions);

                    var mergedRaw = existing.FantraxPositions.ToHashSet(StringComparer.Ordinal);
                    mergedRaw.UnionWith(rawPositions);

                    // Use the most active status when merging.
                    var bestStatus = (int)existing.RosterStatus < (int)rosterStatus
                        ? existing.RosterStatus
                        : rosterStatus;

                    byMlbId[mlbId] = existing with
                    {
                        Positions = merged,
                        FantraxPositions = mergedRaw,
                        RosterStatus = bestStatus,
                    };
                }
                else
                {
                    var teamName = TeamMapping.MlbTeamName(fp.TeamShortName) ?? fp.TeamShortName;
                    byMlbId[mlbId] = new Player(
                        mlbId,
                        NameCleaner.Clean(fp.Name),
                        teamName,
                        positions,
                        rawPositions,
                        rosterStatus);
                }
            }

            Players = [.. byMlbId.Values.OrderBy(p => p.Name, StringComparer.Ordinal)];
            LastSyncDate = _time.GetUtcNow();
            CacheRoster();

            if (headshots is not null) await headshots.PrefetchAsync([.. Players.Select(p => p.Id)], ct);
        }
        catch (Exception ex)
        {
            Error = $"Roster sync failed: {ex.Message}";
            // Keep the last cached roster if available.
        }
        finally
        {
            IsSyncing = false;
        }
    }

    /// <summary>
    /// Determines pitcher vs hitter from Fantrax position strings.
    /// SP, RP, P = pitcher. Everything else = hitter.
    /// </summary>
    private static HashSet<PlayerPosition> ParsePositions(IReadOnlyList<string> positions)
    {
        string[] pitcherCodes = ["SP", "RP", "P"];
        var result = new HashSet<PlayerPosition>();

        foreach (var position in positions)
        {
            var trimmed = position.Trim().ToUpperInvariant();
            result.Add(pitcherCodes.Contains(trimmed) ? PlayerPosition.Pitcher : PlayerPosition.Hitter);
        }

        if (result.Count == 0) result.Add(PlayerPosition.Hitter);   // default to hitter

        return result;
    }

    // MARK: - Caching

    private void CacheRoster()
    {
        var cached = Players.Select(p => new CachedPlayer
        {
            Id = p.Id,
            Name = p.Name,
            Team = p.Team,
            Positions = [.. p.Positions],
            FantraxPositions = [.. p.FantraxPositions],
            RosterStatus = p.RosterStatus,
        });

        settings.RosterCacheJson = JsonSerializer.Serialize(cached, CacheOptions);
    }

    public void LoadCachedRoster()
    {
        if (settings.RosterCacheJson is not { Length: > 0 } json) return;

        List<CachedPlayer>? cached;
        try
        {
            cached = JsonSerializer.Deserialize<List<CachedPlayer>>(json, CacheOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (cached is null) return;

        Players =
        [
            .. cached.Select(c => new Player(
                c.Id,
                c.Name,
                c.Team,
                c.Positions.ToHashSet(),
                (c.FantraxPositions ?? []).ToHashSet(StringComparer.Ordinal),
                c.RosterStatus ?? RosterStatus.Active))
        ];
    }

    private sealed class CachedPlayer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Team { get; set; } = "";
        public List<PlayerPosition> Positions { get; set; } = [];
        public List<string>? FantraxPositions { get; set; }
        public RosterStatus? RosterStatus { get; set; }
    }
}
