import Foundation

enum PlayerState {
    case active(GameContext)
    case upcoming(startTime: Date)
    case inactive(reason: InactiveReason)

    enum ActiveRole {
        case batting
        case pitching
    }

    struct GameContext {
        let gamePk: Int
        let role: ActiveRole
        let inning: String
        let homeTeam: String
        let awayTeam: String
        let homeTeamID: Int
        let awayTeamID: Int
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
        case gameOver(gamePk: Int)
        case dayOff
        case substituted(gamePk: Int)
    }
}
