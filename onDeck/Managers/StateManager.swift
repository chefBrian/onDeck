import Foundation

@Observable
@MainActor
final class StateManager {
    var playerStates: [Int: PlayerState] = [:] // keyed by MLB player ID

    /// Callback fired when a player's state changes. Args: (playerID, oldState, newState)
    var onStateChange: ((Int, PlayerState?, PlayerState) -> Void)?

    func update(playerID: Int, state: PlayerState) {
        let oldState = playerStates[playerID]
        playerStates[playerID] = state
        onStateChange?(playerID, oldState, state)
    }

    func startTime(for playerID: Int) -> Date? {
        if case .upcoming(let time) = playerStates[playerID] { return time }
        return nil
    }

    /// Sets all players to upcoming with a given start time (used when schedule is fetched).
    func setUpcoming(playerIDs: [Int], startTime: Date) {
        for id in playerIDs {
            if playerStates[id] == nil {
                playerStates[id] = .upcoming(startTime: startTime)
            }
        }
    }

    /// Sets all players in a game to inactive (game over).
    func setGameOver(playerIDs: [Int], gamePk: Int) {
        for id in playerIDs {
            update(playerID: id, state: .inactive(reason: .gameOver(gamePk: gamePk)))
        }
    }

    /// Clears all state (e.g., on new day).
    func reset() {
        playerStates.removeAll()
    }
}
