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

    /// Stores the last completed play description per player (for result notifications).
    var lastPlayDescriptions: [Int: String] = [:] // playerID -> description

    /// Latest feed data per game (for In Game player display).
    var latestFeeds: [Int: LiveFeedData] = [:] // gamePk -> feed

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
        while !Task.isCancelled {
            do {
                let feed = try await mlbAPI.fetchLiveFeed(gamePk: gamePk)
                processFeed(feed, gamePk: gamePk, game: game)

                // Stop polling if game is over
                if feed.gameState == "Final" {
                    print("[GameMonitor] Game \(gamePk) is Final - marking players done")
                    let playerIDsInGame = rosterPlayerIDs.filter { id in
                        isPlayerInGame(playerID: id, game: game)
                    }
                    stateManager?.setGameOver(playerIDs: Array(playerIDsInGame))
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

        guard feed.gameState == "Live" else {
            print("[GameMonitor] Game \(gamePk) state: \(feed.gameState) (skipping)")
            return
        }

        let awayShort = game.awayTeam.split(separator: " ").last.map(String.init) ?? game.awayTeam
        let homeShort = game.homeTeam.split(separator: " ").last.map(String.init) ?? game.homeTeam

        let gameContext = PlayerState.GameContext(
            gamePk: gamePk,
            inning: formatInning(feed),
            homeTeam: homeShort,
            awayTeam: awayShort,
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

        // Check if previous pitcher from our roster is no longer active
        if let prevPitcher = lastPitcherID[gamePk],
           prevPitcher != feed.currentPitcherID,
           rosterPlayerIDs.contains(prevPitcher) {
            // Pitcher was pulled - mark as substituted
            stateManager?.update(playerID: prevPitcher, state: .inactive(reason: .substituted))
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
