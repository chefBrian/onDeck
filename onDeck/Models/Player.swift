import Foundation

struct Player: Identifiable, Hashable {
    let id: Int // MLB player ID
    let name: String
    let team: String
    let positions: Set<PlayerPosition>

    var isPitcher: Bool { positions.contains(.pitcher) }
    var isHitter: Bool { positions.contains(.hitter) }

    enum PlayerPosition: String, Hashable, Codable {
        case hitter
        case pitcher
    }
}
