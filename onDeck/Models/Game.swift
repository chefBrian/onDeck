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

    enum Side { case home, away }

    struct Broadcast: Hashable {
        let callSign: String
        let isExclusive: Bool
    }

    func side(for player: Player) -> Side? {
        if homeTeam.contains(player.team) || player.team.contains(homeTeam) { return .home }
        if awayTeam.contains(player.team) || player.team.contains(awayTeam) { return .away }
        return nil
    }
}

/// Lineup IDs tracked per side so consumers can tell whether a player's
/// own team has submitted yet (vs just the opponent). Batters and pitchers
/// are tracked separately: `isSubmitted` reflects the batting card only
/// (pitchers come from a different source and shouldn't gate hitter checks),
/// while `ids(for:)` returns the union so membership tests cover both roles.
struct GameLineup: Equatable {
    var home: Set<Int> = []
    var away: Set<Int> = []
    var homePitchers: Set<Int> = []
    var awayPitchers: Set<Int> = []

    func ids(for side: Game.Side) -> Set<Int> {
        let batters = side == .home ? home : away
        let pitchers = side == .home ? homePitchers : awayPitchers
        return batters.union(pitchers)
    }

    func isSubmitted(for side: Game.Side) -> Bool {
        !(side == .home ? home : away).isEmpty
    }
}
