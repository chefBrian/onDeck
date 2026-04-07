import Foundation

struct Game: Identifiable, Hashable {
    let id: Int // gamePk
    let homeTeam: String
    let awayTeam: String
    let startTime: Date
    let broadcasts: [Broadcast]

    struct Broadcast: Hashable {
        let callSign: String
        let isExclusive: Bool
    }
}
