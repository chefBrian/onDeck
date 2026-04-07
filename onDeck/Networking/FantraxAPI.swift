import Foundation

struct FantraxAPI {
    func fetchRoster(leagueID: String, teamID: String) async throws -> [FantraxPlayer] {
        // TODO: POST to /fxpa/req?leagueId={id} with getTeamRosterInfo
        return []
    }

    struct FantraxPlayer {
        let name: String
        let teamShortName: String
        let positions: [String]
    }
}
