namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/TeamMapping.swift</c>.</summary>
public static class TeamMapping
{
    // Single ordered source of truth. Order matters: the reverse lookup keeps the first
    // abbreviation seen for a given MLB name (ties broken by position, shorter wins),
    // which makes "Athletics" resolve to ATH rather than the legacy OAK. The Swift
    // original iterated a Dictionary, so its reverse map was order-random per process.
    private static readonly (string Abbreviation, string MlbName)[] Pairs =
    [
        ("ARI", "Arizona Diamondbacks"),
        ("ATH", "Athletics"),
        ("ATL", "Atlanta Braves"),
        ("BAL", "Baltimore Orioles"),
        ("BOS", "Boston Red Sox"),
        ("CHC", "Chicago Cubs"),
        ("CHW", "Chicago White Sox"),
        ("CIN", "Cincinnati Reds"),
        ("CLE", "Cleveland Guardians"),
        ("COL", "Colorado Rockies"),
        ("DET", "Detroit Tigers"),
        ("HOU", "Houston Astros"),
        ("KC", "Kansas City Royals"),
        ("LAA", "Los Angeles Angels"),
        ("LAD", "Los Angeles Dodgers"),
        ("MIA", "Miami Marlins"),
        ("MIL", "Milwaukee Brewers"),
        ("MIN", "Minnesota Twins"),
        ("NYM", "New York Mets"),
        ("NYY", "New York Yankees"),
        ("OAK", "Athletics"),           // legacy abbreviation
        ("PHI", "Philadelphia Phillies"),
        ("PIT", "Pittsburgh Pirates"),
        ("SD", "San Diego Padres"),
        ("SEA", "Seattle Mariners"),
        ("SF", "San Francisco Giants"),
        ("STL", "St. Louis Cardinals"),
        ("TB", "Tampa Bay Rays"),
        ("TEX", "Texas Rangers"),
        ("TOR", "Toronto Blue Jays"),
        ("WAS", "Washington Nationals"),
    ];

    /// <summary>Fantrax team abbreviations to MLB API full team names, for disambiguation.</summary>
    public static IReadOnlyDictionary<string, string> FantraxToMlb { get; } =
        Pairs.ToDictionary(pair => pair.Abbreviation, pair => pair.MlbName, StringComparer.Ordinal);

    /// <summary>Reverse lookup: MLB full name to shortest abbreviation, in declaration order.</summary>
    private static readonly (string MlbName, string Abbreviation)[] MlbToAbbreviation = BuildReverse();

    /// <summary>Returns the MLB full team name for a Fantrax abbreviation.</summary>
    public static string? MlbTeamName(string fantraxAbbreviation) =>
        FantraxToMlb.TryGetValue(fantraxAbbreviation.ToUpperInvariant(), out var name) ? name : null;

    /// <summary>Returns a short abbreviation for an MLB full team name, or the last word as fallback.</summary>
    public static string Abbreviation(string mlbTeamName)
    {
        foreach (var (name, abbreviation) in MlbToAbbreviation)
        {
            if (name == mlbTeamName) return abbreviation;
        }

        // Partial match fallback, e.g. "Sacramento Athletics" -> "Athletics" -> ATH.
        foreach (var (name, abbreviation) in MlbToAbbreviation)
        {
            if (mlbTeamName.Contains(name, StringComparison.Ordinal)) return abbreviation;
        }

        var words = mlbTeamName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[^1] : mlbTeamName;
    }

    /// <summary>
    /// Checks if an MLB API team name matches a Fantrax abbreviation. Handles partial
    /// matches, e.g. "Athletics" matches "Sacramento Athletics".
    /// </summary>
    public static bool Matches(string mlbTeamName, string fantraxAbbreviation) =>
        MlbTeamName(fantraxAbbreviation) is { } expected
        && mlbTeamName.Contains(expected, StringComparison.Ordinal);

    private static (string MlbName, string Abbreviation)[] BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (abbreviation, name) in Pairs)
        {
            if (map.TryGetValue(name, out var existing))
            {
                // Keep the shorter abbreviation (e.g. "KC" over "KCR"); ties keep the first.
                if (abbreviation.Length < existing.Length) map[name] = abbreviation;
            }
            else
            {
                map[name] = abbreviation;
                order.Add(name);
            }
        }

        return [.. order.Select(name => (name, map[name]))];
    }
}
