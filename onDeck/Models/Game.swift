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

    struct Broadcast: Hashable {
        let callSign: String
        let isExclusive: Bool
    }
}
