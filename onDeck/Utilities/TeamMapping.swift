import Foundation

enum TeamMapping {
    /// Maps Fantrax team abbreviations to MLB API full team names for disambiguation.
    static let fantraxToMLB: [String: String] = [
        "ARI": "Arizona Diamondbacks",
        "ATH": "Athletics",
        "ATL": "Atlanta Braves",
        "BAL": "Baltimore Orioles",
        "BOS": "Boston Red Sox",
        "CHC": "Chicago Cubs",
        "CHW": "Chicago White Sox",
        "CIN": "Cincinnati Reds",
        "CLE": "Cleveland Guardians",
        "COL": "Colorado Rockies",
        "DET": "Detroit Tigers",
        "HOU": "Houston Astros",
        "KC": "Kansas City Royals",
        "LAA": "Los Angeles Angels",
        "LAD": "Los Angeles Dodgers",
        "MIA": "Miami Marlins",
        "MIL": "Milwaukee Brewers",
        "MIN": "Minnesota Twins",
        "NYM": "New York Mets",
        "NYY": "New York Yankees",
        "OAK": "Athletics", // Legacy abbreviation
        "PHI": "Philadelphia Phillies",
        "PIT": "Pittsburgh Pirates",
        "SD": "San Diego Padres",
        "SEA": "Seattle Mariners",
        "SF": "San Francisco Giants",
        "STL": "St. Louis Cardinals",
        "TB": "Tampa Bay Rays",
        "TEX": "Texas Rangers",
        "TOR": "Toronto Blue Jays",
        "WAS": "Washington Nationals",
    ]

    /// Returns the MLB full team name for a Fantrax abbreviation.
    static func mlbTeamName(for fantraxAbbreviation: String) -> String? {
        fantraxToMLB[fantraxAbbreviation.uppercased()]
    }

    /// Reverse lookup: MLB full name to shortest abbreviation.
    private static let mlbToAbbreviation: [String: String] = {
        var map: [String: String] = [:]
        for (abbrev, name) in fantraxToMLB {
            if let existing = map[name] {
                // Keep the shorter abbreviation (e.g., "KC" over "KCR")
                if abbrev.count < existing.count { map[name] = abbrev }
            } else {
                map[name] = abbrev
            }
        }
        return map
    }()

    /// Returns a short abbreviation for an MLB full team name, or the last word as fallback.
    static func abbreviation(for mlbTeamName: String) -> String {
        if let abbrev = mlbToAbbreviation[mlbTeamName] { return abbrev }
        // Partial match fallback
        for (name, abbrev) in mlbToAbbreviation {
            if mlbTeamName.contains(name) { return abbrev }
        }
        return mlbTeamName.split(separator: " ").last.map(String.init) ?? mlbTeamName
    }

    /// Checks if an MLB API team name matches a Fantrax abbreviation.
    /// Handles partial matches (e.g., "Athletics" matches "Sacramento Athletics").
    static func matches(mlbTeamName: String, fantraxAbbreviation: String) -> Bool {
        guard let expected = fantraxToMLB[fantraxAbbreviation.uppercased()] else { return false }
        return mlbTeamName.contains(expected) || mlbTeamName == expected
    }
}
