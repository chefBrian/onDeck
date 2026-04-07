import Foundation

enum PlayerState {
    case active(GameContext)
    case upcoming(startTime: Date)
    case inactive(reason: InactiveReason)

    struct GameContext {
        let gamePk: Int
        let inning: String
        let score: String
        let count: String
    }

    enum InactiveReason {
        case gameOver
        case dayOff
        case substituted
    }
}
