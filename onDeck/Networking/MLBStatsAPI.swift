import Foundation

struct MLBStatsAPI {
    func searchPlayer(name: String, teamName: String?) async throws -> Int? {
        // TODO: GET statsapi.mlb.com/api/v1/people/search?names={name}&hydrate=currentTeam
        // Returns MLB player ID
        return nil
    }

    func fetchSchedule(date: Date) async throws -> [Game] {
        // TODO: GET statsapi.mlb.com/api/v1/schedule?sportId=1&date={date}&hydrate=team,broadcasts,linescore
        return []
    }

    func fetchLiveFeed(gamePk: Int) async throws -> LiveFeedData {
        // TODO: GET statsapi.mlb.com/api/v1.1/game/{gamePk}/feed/live
        return LiveFeedData()
    }

    struct LiveFeedData {
        // TODO: Current batter, pitcher, inning, score, count, outs
    }
}
