import Foundation

struct Player: Identifiable, Hashable {
    let id: Int // MLB player ID
    let name: String
    let team: String
    let positions: Set<PlayerPosition>
    let fantraxPositions: Set<String> // Original position codes from Fantrax (e.g., "SP", "RP", "C")
    let rosterStatus: RosterStatus

    var isPitcher: Bool { positions.contains(.pitcher) }
    var isHitter: Bool { positions.contains(.hitter) }
    var isOnBench: Bool { rosterStatus == .reserve }
    var isUnavailable: Bool { rosterStatus == .injuredReserve || rosterStatus == .minors }
    var isStartingPitcherOnly: Bool { fantraxPositions.contains("SP") && !fantraxPositions.contains("RP") && !isHitter }

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
