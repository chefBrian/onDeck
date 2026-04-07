import Foundation

struct Player: Identifiable, Hashable {
    let id: Int // MLB player ID
    let name: String
    let team: String
    let position: PlayerPosition

    enum PlayerPosition: Hashable {
        case hitter
        case pitcher
    }
}
