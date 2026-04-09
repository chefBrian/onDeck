import Foundation

struct Game: Identifiable, Hashable {
    let id: Int // gamePk
    let homeTeam: String
    let awayTeam: String
    let homeTeamID: Int
    let awayTeamID: Int
    let startTime: Date
    let homeProbablePitcherID: Int?
    let awayProbablePitcherID: Int?
    let broadcasts: [Broadcast]
    let homeLineup: [Int] // batting order player IDs from schedule (empty if not yet submitted)
    let awayLineup: [Int]

    struct Broadcast: Hashable {
        let callSign: String
        let isExclusive: Bool
    }
}
