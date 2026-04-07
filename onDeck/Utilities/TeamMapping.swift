import Foundation

enum TeamMapping {
    /// Maps Fantrax team abbreviations to MLB API team names for disambiguation.
    static let fantraxToMLB: [String: String] = [
        "ARI": "D-backs",
        "ATH": "Athletics",
        "ATL": "Braves",
        "BAL": "Orioles",
        "BOS": "Red Sox",
        "CHC": "Cubs",
        "CHW": "White Sox",
        "CIN": "Reds",
        "CLE": "Guardians",
        "COL": "Rockies",
        "DET": "Tigers",
        "HOU": "Astros",
        "KC": "Royals",
        "LAA": "Angels",
        "LAD": "Dodgers",
        "MIA": "Marlins",
        "MIL": "Brewers",
        "MIN": "Twins",
        "NYM": "Mets",
        "NYY": "Yankees",
        "OAK": "Athletics", // Legacy abbreviation
        "PHI": "Phillies",
        "PIT": "Pirates",
        "SD": "Padres",
        "SEA": "Mariners",
        "SF": "Giants",
        "STL": "Cardinals",
        "TB": "Rays",
        "TEX": "Rangers",
        "TOR": "Blue Jays",
        "WAS": "Nationals",
    ]

    static func mlbTeamName(for fantraxAbbreviation: String) -> String? {
        fantraxToMLB[fantraxAbbreviation.uppercased()]
    }
}
