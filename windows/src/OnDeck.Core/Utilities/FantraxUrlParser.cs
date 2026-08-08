using System.Web;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/FantraxURLParser.swift</c>.</summary>
public static class FantraxUrlParser
{
    public sealed record ParsedUrl(string LeagueId, string? TeamId);

    public static ParsedUrl? Parse(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url)) return null;

        // Try query parameters first (newui format).
        var query = HttpUtility.ParseQueryString(url.Query);
        var leagueId = query["leagueId"];
        var teamId = query["teamId"];

        // Try path-based extraction for leagueId: /league/{id}/
        if (string.IsNullOrEmpty(leagueId))
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var leagueIndex = Array.IndexOf(segments, "league");
            if (leagueIndex >= 0 && leagueIndex + 1 < segments.Length)
            {
                leagueId = segments[leagueIndex + 1];
            }
        }

        // Try matrix parameters for teamId: ;teamId={id}
        if (string.IsNullOrEmpty(teamId))
        {
            const string marker = ";teamId=";
            var start = urlString.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0)
            {
                var rest = urlString[(start + marker.Length)..];
                var end = rest.AsSpan().IndexOfAny('&', ';', '/');
                var value = end >= 0 ? rest[..end] : rest;
                if (value.Length > 0) teamId = value;
            }
        }

        if (string.IsNullOrEmpty(leagueId)) return null;

        return new ParsedUrl(leagueId, string.IsNullOrEmpty(teamId) ? null : teamId);
    }
}
