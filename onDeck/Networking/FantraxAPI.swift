import Foundation

struct FantraxAPI: Sendable {
    struct FantraxPlayer: Sendable {
        let name: String
        let teamShortName: String
        let positions: [String]
    }

    enum FantraxError: Error, LocalizedError {
        case invalidResponse
        case httpError(Int)

        var errorDescription: String? {
            switch self {
            case .invalidResponse: return "Invalid response from Fantrax"
            case .httpError(let code): return "Fantrax API returned HTTP \(code)"
            }
        }
    }

    func fetchRoster(leagueID: String, teamID: String) async throws -> [FantraxPlayer] {
        let url = URL(string: "https://www.fantrax.com/fxpa/req?leagueId=\(leagueID)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("text/plain", forHTTPHeaderField: "Content-Type")

        let body: [String: Any] = [
            "msgs": [["method": "getTeamRosterInfo", "data": ["leagueId": leagueID, "teamId": teamID]]],
            "uiv": 3
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await URLSession.shared.data(for: request)

        if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode != 200 {
            throw FantraxError.httpError(httpResponse.statusCode)
        }

        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw FantraxError.invalidResponse
        }

        var players: [FantraxPlayer] = []
        findScorers(in: json, players: &players)

        if players.isEmpty {
            throw FantraxError.invalidResponse
        }

        return players
    }

    /// Recursively walks the JSON tree to find scorer objects.
    /// Scorer objects have at minimum `scorerId` and `name` fields.
    /// This approach is robust against response structure changes.
    private func findScorers(in object: Any, players: inout [FantraxPlayer]) {
        if let dict = object as? [String: Any] {
            if let name = dict["name"] as? String,
               dict["scorerId"] != nil {
                let teamShortName = dict["teamShortName"] as? String ?? ""
                let posString = dict["posShortNames"] as? String ?? ""
                let positions = posString.split(separator: ",").map { String($0).trimmingCharacters(in: .whitespaces) }
                players.append(FantraxPlayer(name: name, teamShortName: teamShortName, positions: positions))
            }
            for value in dict.values {
                findScorers(in: value, players: &players)
            }
        } else if let array = object as? [Any] {
            for item in array {
                findScorers(in: item, players: &players)
            }
        }
    }
}
