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

    func configure(stateManager: StateManager) {
        self.stateManager = stateManager
    }

    func startMonitoring(games: [Game], players: [Player]) {
        stopMonitoring()

        rosterPlayerIDs = Set(players.map(\.id))
        rosterPlayers = Dictionary(uniqueKeysWithValues: players.map { ($0.id, $0) })
        isMonitoring = true

        for game in games {
            let gamePk = game.id
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
        guard feed.gameState == "Live" else { return }

        let gameContext = PlayerState.GameContext(
            gamePk: gamePk,
            inning: formatInning(feed),
            score: formatScore(feed, game: game),
            count: formatCount(feed)
        )

        // Check current batter
        if let batterID = feed.currentBatterID, rosterPlayerIDs.contains(batterID) {
            stateManager?.update(playerID: batterID, state: .active(gameContext))
        }

        // Check current pitcher
        if let pitcherID = feed.currentPitcherID, rosterPlayerIDs.contains(pitcherID) {
            stateManager?.update(playerID: pitcherID, state: .active(gameContext))
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
        return "\(half) \(inning)"
    }

    private func formatScore(_ feed: LiveFeedData, game: Game) -> String {
        let away = game.awayTeam.split(separator: " ").last.map(String.init) ?? game.awayTeam
        let home = game.homeTeam.split(separator: " ").last.map(String.init) ?? game.homeTeam
        return "\(away) \(feed.awayScore), \(home) \(feed.homeScore)"
    }

    private func formatCount(_ feed: LiveFeedData) -> String {
        return "\(feed.balls)-\(feed.strikes), \(feed.outs) out\(feed.outs == 1 ? "" : "s")"
    }
}
