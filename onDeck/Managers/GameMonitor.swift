import Foundation

@Observable
@MainActor
final class GameMonitor {
    var isMonitoring = false

    private let mlbAPI = MLBStatsAPI()
    private var pollingTasks: [Int: Task<Void, Never>] = [:] // keyed by gamePk
    private var rosterPlayerIDs: Set<Int> = []
    private var rosterPlayers: [Int: Player] = [:]
    private weak var stateManager: StateManager?

    /// Tracks previously seen batter/pitcher per game to detect transitions.
    private var lastBatterID: [Int: Int] = [:] // gamePk -> batterID
    private var lastPitcherID: [Int: Int] = [:] // gamePk -> pitcherID
    private var lastHomePitcherID: [Int: Int] = [:] // gamePk -> last pitcher for home team
    private var lastAwayPitcherID: [Int: Int] = [:] // gamePk -> last pitcher for away team

    /// Stores the last completed play description per player (for result notifications).
    var lastPlayDescriptions: [Int: String] = [:] // playerID -> description

    /// Latest feed data per game (for In Game player display).
    var latestFeeds: [Int: LiveFeedData] = [:] // gamePk -> feed

    /// Lineup player IDs per game (batting order + pitchers).
    var lineupPlayerIDs: [Int: Set<Int>] = [:] // gamePk -> set of player IDs in lineup

    func configure(stateManager: StateManager) {
        self.stateManager = stateManager
    }

    func startMonitoring(games: [Game], players: [Player]) {
        stopMonitoring()

        rosterPlayerIDs = Set(players.map(\.id))
        rosterPlayers = Dictionary(uniqueKeysWithValues: players.map { ($0.id, $0) })
        isMonitoring = true

        print("[GameMonitor] Starting monitoring for \(games.count) games")
        print("[GameMonitor] Watching \(rosterPlayerIDs.count) roster player IDs: \(rosterPlayerIDs.sorted())")
        for player in players {
            print("[GameMonitor]   \(player.id) = \(player.name) (\(player.team))")
        }

        for game in games {
            let gamePk = game.id
            print("[GameMonitor] Polling game \(gamePk): \(game.awayTeam) @ \(game.homeTeam)")
            pollingTasks[gamePk] = Task { [weak self] in
                await self?.pollGame(gamePk: gamePk, game: game)
            }
        }
    }

    func stopMonitoring() {
        for task in pollingTasks.values {
            task.cancel()
        }
        pollingTasks.removeAll()
        lastBatterID.removeAll()
        lastPitcherID.removeAll()
        lastHomePitcherID.removeAll()
        lastAwayPitcherID.removeAll()
        lineupPlayerIDs.removeAll()
        isMonitoring = false
    }

    /// Stops monitoring a specific game (e.g., when no roster players remain).
    func stopMonitoring(gamePk: Int) {
        pollingTasks[gamePk]?.cancel()
        pollingTasks.removeValue(forKey: gamePk)
        if pollingTasks.isEmpty {
            isMonitoring = false
        }
    }

    // MARK: - Polling Loop

    private func pollGame(gamePk: Int, game: Game) async {
        // Wait until 15 minutes before game start
        let pollStart = game.startTime.addingTimeInterval(-15 * 60)
        let delay = pollStart.timeIntervalSinceNow
        if delay > 0 {
            print("[GameMonitor] Game \(gamePk) starts at \(game.startTime) - sleeping until \(pollStart)")
            do {
                try await Task.sleep(for: .seconds(delay))
            } catch {
                return // Task cancelled
            }
            print("[GameMonitor] Game \(gamePk) - waking up, starting poll loop")
        }

        while !Task.isCancelled {
            do {
                let feed = try await mlbAPI.fetchLiveFeed(gamePk: gamePk)
                processFeed(feed, gamePk: gamePk, game: game)

                // Stop polling if game is over
                if feed.gameState == "Final" {
                    let playerIDsInGame = rosterPlayerIDs.filter { id in
                        isPlayerInGame(playerID: id, game: game)
                    }
                    print("[GameMonitor] Game \(gamePk) is Final - marking done: \(playerIDsInGame)")
                    stateManager?.setGameOver(playerIDs: Array(playerIDsInGame), gamePk: gamePk)
                    stopMonitoring(gamePk: gamePk)
                    return
                }
            } catch {
                // Log error but keep polling
                print("Live feed error for game \(gamePk): \(error)")
            }

            do {
                try await Task.sleep(for: .seconds(5))
            } catch {
                return // Task cancelled
            }
        }
    }

    // MARK: - Feed Processing

    private func processFeed(_ feed: LiveFeedData, gamePk: Int, game: Game) {
        latestFeeds[gamePk] = feed

        // Track lineup (available before game goes Live)
        let lineupIDs = Set(feed.homeBattingOrder + feed.awayBattingOrder + feed.homePitchers + feed.awayPitchers)
        if !lineupIDs.isEmpty {
            lineupPlayerIDs[gamePk] = lineupIDs
        }

        guard feed.gameState == "Live", feed.detailedState == "In Progress" else {
            print("[GameMonitor] Game \(gamePk) state: \(feed.gameState)/\(feed.detailedState ?? "nil") (skipping)")
            return
        }

        let awayShort = game.awayTeam.split(separator: " ").last.map(String.init) ?? game.awayTeam
        let homeShort = game.homeTeam.split(separator: " ").last.map(String.init) ?? game.homeTeam

        let gameContext = PlayerState.GameContext(
            gamePk: gamePk,
            inning: formatInning(feed),
            homeTeam: homeShort,
            awayTeam: awayShort,
            homeTeamID: feed.homeTeamID,
            awayTeamID: feed.awayTeamID,
            homeScore: feed.homeScore,
            awayScore: feed.awayScore,
            balls: feed.balls,
            strikes: feed.strikes,
            outs: feed.outs,
            runnerOnFirst: feed.runnerOnFirst,
            runnerOnSecond: feed.runnerOnSecond,
            runnerOnThird: feed.runnerOnThird
        )

        // Check current batter
        if let batterID = feed.currentBatterID {
            if rosterPlayerIDs.contains(batterID) {
                let isNew = lastBatterID[gamePk] != batterID
                print("[GameMonitor] >>> ROSTER BATTER: \(feed.currentBatterName ?? "?") (ID \(batterID)) - \(isNew ? "NEW" : "same") - \(formatInning(feed))")
                stateManager?.update(playerID: batterID, state: .active(gameContext))
            } else {
                // Only log occasionally to reduce noise
                if lastBatterID[gamePk] != batterID {
                    print("[GameMonitor] Batter \(feed.currentBatterName ?? "?") (ID \(batterID)) not on roster")
                }
            }
        }

        // Check current pitcher
        if let pitcherID = feed.currentPitcherID {
            if rosterPlayerIDs.contains(pitcherID) {
                let isNew = lastPitcherID[gamePk] != pitcherID
                print("[GameMonitor] >>> ROSTER PITCHER: \(feed.currentPitcherName ?? "?") (ID \(pitcherID)) - \(isNew ? "NEW" : "same") - \(formatInning(feed))")
                stateManager?.update(playerID: pitcherID, state: .active(gameContext))
            }
        }

        // Check if previous batter from our roster is no longer active
        if let prevBatter = lastBatterID[gamePk],
           prevBatter != feed.currentBatterID,
           rosterPlayerIDs.contains(prevBatter) {
            // Previous batter's at-bat is done - move back to upcoming
            stateManager?.update(playerID: prevBatter, state: .upcoming(startTime: game.startTime))
        }

        // Track pitcher per team side and detect substitutions
        if let pitcherID = feed.currentPitcherID {
            let isHome = feed.homePitchers.contains(pitcherID)
            if isHome {
                if let prev = lastHomePitcherID[gamePk], prev != pitcherID, rosterPlayerIDs.contains(prev) {
                    stateManager?.update(playerID: prev, state: .inactive(reason: .substituted(gamePk: gamePk)))
                }
                lastHomePitcherID[gamePk] = pitcherID
            } else {
                if let prev = lastAwayPitcherID[gamePk], prev != pitcherID, rosterPlayerIDs.contains(prev) {
                    stateManager?.update(playerID: prev, state: .inactive(reason: .substituted(gamePk: gamePk)))
                }
                lastAwayPitcherID[gamePk] = pitcherID
            }
        }

        // Check if previous pitcher from our roster is no longer active (half-inning change)
        if let prevPitcher = lastPitcherID[gamePk],
           prevPitcher != feed.currentPitcherID,
           rosterPlayerIDs.contains(prevPitcher) {
            // Only set to upcoming if not already marked as substituted
            let currentState = stateManager?.playerStates[prevPitcher]
            if case .inactive(.substituted) = currentState {
                // Already substituted, don't revert
            } else {
                stateManager?.update(playerID: prevPitcher, state: .upcoming(startTime: game.startTime))
            }
        }

        // Catch-all: check both sides using the last pitcher in each pitchers array
        // (boxscore pitchers are ordered by appearance, last = current for that side).
        // Any roster pitcher who pitched earlier but isn't the latest for their side
        // has been substituted. Handles app restarts and missed transitions.
        for pitchers in [feed.homePitchers, feed.awayPitchers] {
            guard let currentForSide = pitchers.last else { continue }
            for id in rosterPlayerIDs {
                guard id != currentForSide,
                      pitchers.contains(id),
                      feed.playerStats[id]?.pitchingLine != nil,
                      let player = rosterPlayers[id],
                      player.isPitcher && !player.isHitter else { continue }
                let currentState = stateManager?.playerStates[id]
                if case .inactive(.substituted) = currentState { continue }
                if case .active = currentState { continue }
                stateManager?.update(playerID: id, state: .inactive(reason: .substituted(gamePk: gamePk)))
            }
        }

        // Store completed play results for notifications
        if feed.isPlayComplete, let desc = feed.lastPlayDescription {
            if let batterID = feed.currentBatterID, rosterPlayerIDs.contains(batterID) {
                lastPlayDescriptions[batterID] = desc
            }
            if let pitcherID = feed.currentPitcherID, rosterPlayerIDs.contains(pitcherID) {
                lastPlayDescriptions[pitcherID] = desc
            }
        }

        lastBatterID[gamePk] = feed.currentBatterID
        lastPitcherID[gamePk] = feed.currentPitcherID
    }

    // MARK: - Helpers

    private func isPlayerInGame(playerID: Int, game: Game) -> Bool {
        guard let player = rosterPlayers[playerID] else { return false }
        return game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
            || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
    }

    private func formatInning(_ feed: LiveFeedData) -> String {
        guard let inning = feed.inning, let half = feed.inningHalf else { return "" }
        let shortHalf = half == "Top" ? "Top" : "Bot"
        return "\(shortHalf) \(inning)"
    }
}
