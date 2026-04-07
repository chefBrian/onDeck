import Foundation

enum FantraxURLParser {
    struct ParsedURL {
        let leagueID: String
        let teamID: String
    }

    static func parse(_ urlString: String) -> ParsedURL? {
        guard let url = URL(string: urlString),
              let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else {
            return nil
        }

        var leagueID: String?
        var teamID: String?

        // Try query parameters first (newui format)
        leagueID = components.queryItems?.first(where: { $0.name == "leagueId" })?.value
        teamID = components.queryItems?.first(where: { $0.name == "teamId" })?.value

        // Try path-based extraction for leagueId: /league/{id}/
        if leagueID == nil {
            let pathSegments = url.pathComponents
            if let leagueIndex = pathSegments.firstIndex(of: "league"),
               leagueIndex + 1 < pathSegments.count {
                leagueID = pathSegments[leagueIndex + 1]
            }
        }

        // Try matrix parameters for teamId: ;teamId={id}
        if teamID == nil {
            let fullString = url.absoluteString
            if let range = fullString.range(of: ";teamId=") {
                let afterParam = fullString[range.upperBound...]
                let value = afterParam.prefix(while: { $0 != "&" && $0 != ";" && $0 != "/" })
                if !value.isEmpty {
                    teamID = String(value)
                }
            }
        }

        guard let leagueID, let teamID else { return nil }
        return ParsedURL(leagueID: leagueID, teamID: teamID)
    }
}
