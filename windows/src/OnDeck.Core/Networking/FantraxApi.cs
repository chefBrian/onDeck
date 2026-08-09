using System.Text;
using System.Text.Json;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Networking;

/// <summary>Port of <c>Networking/FantraxAPI.swift</c>.</summary>
public sealed class FantraxApi(HttpClient http, TimeProvider timeProvider)
{
    public FantraxApi(HttpClient http) : this(http, TimeProvider.System) { }

    // MARK: - Fetch Teams

    /// <summary>Fetches the list of teams in a league using <c>getStandings</c>.</summary>
    public async Task<IReadOnlyList<FantraxTeam>> FetchTeamsAsync(
        string leagueId, CancellationToken ct = default)
    {
        using var document = await PostRequestAsync(
            leagueId, "getStandings", new Dictionary<string, string> { ["leagueId"] = leagueId }, ct);

        var teams = new List<FantraxTeam>();
        FindTeams(document.RootElement, teams);

        if (teams.Count == 0) throw FantraxException.NoTeamsFound();

        // Deduplicate by teamId, keeping the first occurrence.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return
        [
            .. teams.Where(team => seen.Add(team.Id))
                    .OrderBy(team => team.Name, StringComparer.Ordinal)
        ];
    }

    // MARK: - Fetch Roster

    public async Task<IReadOnlyList<FantraxPlayer>> FetchRosterAsync(
        string leagueId, string teamId, CancellationToken ct = default)
    {
        // getTeamRosterInfo defaults to NEXT period's lineup, not today's - so discover
        // today's period from the first call, then re-fetch pinned to it.
        var data = new Dictionary<string, string> { ["leagueId"] = leagueId, ["teamId"] = teamId };

        using var initial = await PostRequestAsync(leagueId, "getTeamRosterInfo", data, ct);
        var todayPeriod = FindTodayPeriod(initial.RootElement);

        JsonDocument? refetched = null;
        try
        {
            var document = initial;
            if (todayPeriod is not null)
            {
                var pinned = new Dictionary<string, string>(data) { ["period"] = todayPeriod };
                refetched = await PostRequestAsync(leagueId, "getTeamRosterInfo", pinned, ct);
                document = refetched;
            }

            var players = ParseRoster(document.RootElement);
            if (players.Count == 0) throw FantraxException.InvalidResponse();

            return players;
        }
        finally
        {
            refetched?.Dispose();
        }
    }

    /// <summary>
    /// Navigate to <c>responses[0].data.tables</c> and extract the top-level <c>scorer</c>
    /// from each row. Don't recurse into cells — they contain opposing pitcher popovers.
    /// </summary>
    private static List<FantraxPlayer> ParseRoster(JsonElement root)
    {
        var players = new List<FantraxPlayer>();

        if (!TryGetFirstResponseData(root, out var data)) return players;
        if (!data.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
        {
            return players;
        }

        foreach (var table in tables.EnumerateArray())
        {
            if (!table.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty("scorer", out var scorer) || scorer.ValueKind != JsonValueKind.Object) continue;
                if (!scorer.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                if (!scorer.TryGetProperty("scorerId", out _)) continue;

                var positionText = StringOrEmpty(scorer, "posShortNames");
                string[] positions = positionText.Length == 0
                    ? []
                    : [.. positionText.Split(',').Select(part => part.Trim())];

                players.Add(new FantraxPlayer(
                    name.GetString()!,
                    StringOrEmpty(scorer, "teamShortName"),
                    positions,
                    StatusId(row)));
            }
        }

        return players;
    }

    private static int StatusId(JsonElement row)
    {
        if (!row.TryGetProperty("statusId", out var status)) return 1;

        return status.ValueKind switch
        {
            JsonValueKind.Number when status.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(status.GetString(), out var parsed) => parsed,
            _ => 1,
        };
    }

    private static string StringOrEmpty(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // MARK: - Period Detection

    /// <summary>
    /// Parses <c>periodList</c> from the API response to find today's period number. Entries
    /// look like "14 (Tue Apr 7)". Uses the baseball day (before 8 AM = yesterday).
    /// </summary>
    private string? FindTodayPeriod(JsonElement root)
    {
        if (!TryGetFirstResponseData(root, out var data)) return null;
        if (!data.TryGetProperty("displayedLists", out var lists) || lists.ValueKind != JsonValueKind.Object) return null;
        if (!lists.TryGetProperty("periodList", out var periodList) || periodList.ValueKind != JsonValueKind.Array) return null;

        var target = BaseballCalendar.Today(timeProvider);

        // Fantrax emits English month abbreviations regardless of locale.
        string[] monthAbbreviations =
            ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

        // Match "14 (Tue Apr 7)" - look for "Mon D)" or "Mon DD)".
        var suffix = $"{monthAbbreviations[target.Month - 1]} {target.Day})";

        foreach (var entry in periodList.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) continue;
            if (entry.GetString() is not { } text) continue;
            if (!text.Contains(suffix, StringComparison.Ordinal)) continue;

            // Extract the period number (everything before the first space).
            var space = text.IndexOf(' ');
            if (space > 0) return text[..space];
        }

        return null;
    }

    private static bool TryGetFirstResponseData(JsonElement root, out JsonElement data)
    {
        data = default;

        if (!root.TryGetProperty("responses", out var responses)
            || responses.ValueKind != JsonValueKind.Array
            || responses.GetArrayLength() == 0)
        {
            return false;
        }

        var first = responses[0];
        if (first.ValueKind != JsonValueKind.Object
            || !first.TryGetProperty("data", out var candidate)
            || candidate.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        data = candidate;
        return true;
    }

    // MARK: - Network

    /// <summary>
    /// Fantrax's edge rejects any request that carries no <c>User-Agent</c> at all with a 403 —
    /// the value is not inspected, only its presence, so this identifies the app honestly rather
    /// than impersonating a browser. Swift never had to set it: <c>URLSession</c> always sends a
    /// default one, while .NET's <c>HttpClient</c> sends none.
    /// </summary>
    private const string UserAgent = "onDeck/1.0";

    private async Task<JsonDocument> PostRequestAsync(
        string leagueId, string method, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var url = $"https://www.fantrax.com/fxpa/req?leagueId={leagueId}";

        var body = JsonSerializer.Serialize(new
        {
            msgs = new[] { new { method, data } },
            uiv = 3,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw FantraxException.HttpError((int)response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw FantraxException.InvalidResponse();
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw FantraxException.InvalidResponse();
        }

        return document;
    }

    // MARK: - JSON Walkers

    /// <summary>
    /// Recursively walks the JSON tree to find team objects. In the standings response, teams
    /// have <c>teamId</c> and <c>content</c> (team name) fields.
    /// </summary>
    private static void FindTeams(JsonElement element, List<FantraxTeam> teams)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("teamId", out var teamId)
                    && teamId.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String
                    && teamId.GetString() is { Length: > 0 } id
                    && content.GetString() is { Length: > 0 } name)
                {
                    teams.Add(new FantraxTeam(id, name));
                }

                foreach (var property in element.EnumerateObject()) FindTeams(property.Value, teams);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) FindTeams(item, teams);
                break;
        }
    }
}
