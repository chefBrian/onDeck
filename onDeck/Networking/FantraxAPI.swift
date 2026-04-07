import Foundation

struct FantraxAPI: Sendable {
    struct FantraxPlayer: Sendable {
        let name: String
        let teamShortName: String
        let positions: [String]
    }

    struct FantraxTeam: Sendable, Identifiable, Hashable {
        let id: String // teamId
        let name: String
    }

    enum FantraxError: Error, LocalizedError {
        case invalidResponse
        case httpError(Int)
        case noTeamsFound

        var errorDescription: String? {
            switch self {
            case .invalidResponse: return "Invalid response from Fantrax"
            case .httpError(let code): return "Fantrax API returned HTTP \(code)"
            case .noTeamsFound: return "No teams found in league"
            }
        }
    }

    // MARK: - Fetch Teams

    /// Fetches the list of teams in a league using getStandings.
    func fetchTeams(leagueID: String) async throws -> [FantraxTeam] {
        let json = try await postRequest(leagueID: leagueID, method: "getStandings", data: ["leagueId": leagueID])

        var teams: [FantraxTeam] = []
        findTeams(in: json, teams: &teams)

        if teams.isEmpty {
            throw FantraxError.noTeamsFound
        }

        // Deduplicate by teamId
        var seen = Set<String>()
        teams = teams.filter { seen.insert($0.id).inserted }

        return teams.sorted { $0.name < $1.name }
    }

    // MARK: - Fetch Roster

    func fetchRoster(leagueID: String, teamID: String) async throws -> [FantraxPlayer] {
        let json = try await postRequest(leagueID: leagueID, method: "getTeamRosterInfo", data: ["leagueId": leagueID, "teamId": teamID])

        var players: [FantraxPlayer] = []
        findScorers(in: json, players: &players)

        if players.isEmpty {
            throw FantraxError.invalidResponse
        }

        return players
    }

    // MARK: - Network

    private func postRequest(leagueID: String, method: String, data: [String: Any]) async throws -> [String: Any] {
        let url = URL(string: "https://www.fantrax.com/fxpa/req?leagueId=\(leagueID)")!
        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("text/plain", forHTTPHeaderField: "Content-Type")

        let body: [String: Any] = [
            "msgs": [["method": method, "data": data]],
            "uiv": 3
        ]
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (responseData, response) = try await URLSession.shared.data(for: request)

        if let httpResponse = response as? HTTPURLResponse, httpResponse.statusCode != 200 {
            throw FantraxError.httpError(httpResponse.statusCode)
        }

        guard let json = try JSONSerialization.jsonObject(with: responseData) as? [String: Any] else {
            throw FantraxError.invalidResponse
        }

        return json
    }

    // MARK: - JSON Walkers

    /// Recursively walks the JSON tree to find scorer objects.
    /// Scorer objects have at minimum `scorerId` and `name` fields.
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

    /// Recursively walks the JSON tree to find team objects.
    /// In the standings response, teams have `teamId` and `content` (team name) fields.
    private func findTeams(in object: Any, teams: inout [FantraxTeam]) {
        if let dict = object as? [String: Any] {
            if let teamId = dict["teamId"] as? String,
               let name = dict["content"] as? String,
               !teamId.isEmpty, !name.isEmpty {
                teams.append(FantraxTeam(id: teamId, name: name))
            }
            for value in dict.values {
                findTeams(in: value, teams: &teams)
            }
        } else if let array = object as? [Any] {
            for item in array {
                findTeams(in: item, teams: &teams)
            }
        }
    }
}
