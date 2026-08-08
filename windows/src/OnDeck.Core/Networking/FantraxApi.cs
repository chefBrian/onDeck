using System.Text;
using System.Text.Json;

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

    // MARK: - Network

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
