import Foundation

struct Player: Identifiable, Hashable {
    let id: Int // MLB player ID
    let name: String
    let team: String
    let positions: Set<PlayerPosition>
    let rosterStatus: RosterStatus

    var isPitcher: Bool { positions.contains(.pitcher) }
    var isHitter: Bool { positions.contains(.hitter) }
    var isOnBench: Bool { rosterStatus != .active }

    enum PlayerPosition: String, Hashable, Codable {
        case hitter
        case pitcher
    }

    enum RosterStatus: Int, Hashable, Codable {
        case active = 1
        case reserve = 2
        case injuredReserve = 3
        case minors = 9
    }
}
