import Foundation

enum PlayerState {
    case active(GameContext)
    case upcoming(startTime: Date)
    case inactive(reason: InactiveReason)

    struct GameContext {
        let gamePk: Int
        let inning: String
        let homeTeam: String
        let awayTeam: String
        let homeScore: Int
        let awayScore: Int
        let balls: Int
        let strikes: Int
        let outs: Int
        let runnerOnFirst: Bool
        let runnerOnSecond: Bool
        let runnerOnThird: Bool
    }

    enum InactiveReason {
        case gameOver
        case dayOff
        case substituted
    }
}
