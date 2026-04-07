import Foundation

@Observable
final class StateManager {
    var playerStates: [Int: PlayerState] = [:] // keyed by MLB player ID

    func update(playerID: Int, state: PlayerState) {
        playerStates[playerID] = state
    }
}
