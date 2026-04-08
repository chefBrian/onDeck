import Foundation

@Observable
@MainActor
final class GameMonitor {
    var isMonitoring = false

    private let mlbAPI = MLBStatsAPI()
    private var coordinatorTask: Task<Void, Never>?
    private var monitoredGames: [Int: Game] = [:] // gamePk -> Game
    private var rosterPlayerIDs: Set<Int> = []
    private var rosterPlayers: [Int: Player] = [:]
    private weak var stateManager: StateManager?

    /// Tracks previously seen batter/pitcher per game to detect transitions.
    private var lastBatterID: [Int: Int] = [:] // gamePk -> batterID
    private var lastPitcherID: [Int: Int] = [:] // gamePk -> pitcherID
    private var lastHomePitcherID: [Int: Int] = [:] // gamePk -> last pitcher for home team
    private var lastAwayPitcherID: [Int: Int] = [:] // gamePk -> last pitcher for away team

    /// Cached raw feed data for diffPatch optimization.
    private var cachedFeedData: [Int: Data] = [:] // gamePk -> raw JSON bytes
    private var cachedTimecodes: [Int: String] = [:] // gamePk -> metaData.timeStamp

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
        monitoredGames = Dictionary(uniqueKeysWithValues: games.map { ($0.id, $0) })
        isMonitoring = true

        print("[GameMonitor] Starting monitoring for \(games.count) games")
        print("[GameMonitor] Watching \(rosterPlayerIDs.count) roster player IDs: \(rosterPlayerIDs.sorted())")
        for player in players {
            print("[GameMonitor]   \(player.id) = \(player.name) (\(player.team))")
        }

        for game in games {
            print("[GameMonitor] Monitoring game \(game.id): \(game.awayTeam) @ \(game.homeTeam)")
        }

        coordinatorTask = Task { [weak self] in
            await self?.coordinatePolling()
        }
    }

    func stopMonitoring() {
        coordinatorTask?.cancel()
        coordinatorTask = nil
        monitoredGames.removeAll()
        lastBatterID.removeAll()
        lastPitcherID.removeAll()
        lastHomePitcherID.removeAll()
        lastAwayPitcherID.removeAll()
        lineupPlayerIDs.removeAll()
        cachedFeedData.removeAll()
        cachedTimecodes.removeAll()
        isMonitoring = false
    }

    /// Stops monitoring a specific game (e.g., when no roster players remain).
    func stopMonitoring(gamePk: Int) {
        monitoredGames.removeValue(forKey: gamePk)
        if monitoredGames.isEmpty {
            coordinatorTask?.cancel()
            coordinatorTask = nil
            isMonitoring = false
        }
    }

    // MARK: - Centralized Polling

    private func coordinatePolling() async {
        // Wait until the earliest game is within 15 minutes of starting
        let earliestStart = monitoredGames.values.map(\.startTime).min() ?? .now
        let pollStart = earliestStart.addingTimeInterval(-15 * 60)
        let delay = pollStart.timeIntervalSinceNow
        if delay > 0 {
            print("[GameMonitor] Earliest game at \(earliestStart) - sleeping until \(pollStart)")
            do {
                try await Task.sleep(for: .seconds(delay), tolerance: .seconds(30))
            } catch {
                return
            }
            print("[GameMonitor] Waking up, starting coordinator loop")
        }

        while !Task.isCancelled {
            await pollCycle()

            do {
                try await Task.sleep(for: .seconds(10), tolerance: .seconds(2))
            } catch {
                return
            }
        }
    }

    private func pollCycle() async {
        let activeGamePks = monitoredGames.keys.filter { gamePk in
            guard let game = monitoredGames[gamePk] else { return false }
            return game.startTime.addingTimeInterval(-15 * 60) <= Date.now
        }
        guard !activeGamePks.isEmpty else { return }

        await withTaskGroup(of: Void.self) { group in
            for gamePk in activeGamePks {
                guard let game = monitoredGames[gamePk] else { continue }
                let cached = cachedFeedData[gamePk]
                let timecode = cachedTimecodes[gamePk]

                group.addTask { [weak self] in
                    guard let self else { return }
                    await self.pollSingleGame(gamePk: gamePk, game: game, cachedData: cached, timecode: timecode)
                }
            }
        }
    }

    private func pollSingleGame(gamePk: Int, game: Game, cachedData: Data?, timecode: String?) async {
        let label = "\(TeamMapping.abbreviation(for: game.awayTeam))@\(TeamMapping.abbreviation(for: game.homeTeam))"
        do {
            let feed: LiveFeedData

            if let cachedData, let timecode {
                let result = try await mlbAPI.fetchDiffPatch(gamePk: gamePk, since: timecode, label: label)

                switch result {
                case .noChanges:
                    return

                case .patches(let patches):
                    var json = try JSONSerialization.jsonObject(with: cachedData)
                    try JSONPatch.apply(patches, to: &json)
                    let newData = try JSONSerialization.data(withJSONObject: json)
                    let (decoded, newTimecode) = try MLBStatsAPI.decodeLiveFeed(from: newData)
                    feed = decoded
                    cachedFeedData[gamePk] = newData
                    if let newTimecode { cachedTimecodes[gamePk] = newTimecode }

                case .fullUpdate(let rawData):
                    // API returns full feed during game phase transitions (inning changes, etc.)
                    // Decode directly instead of re-fetching - patches resume next cycle
                    let (decoded, newTimecode) = try MLBStatsAPI.decodeLiveFeed(from: rawData)
                    feed = decoded
                    cachedFeedData[gamePk] = rawData
                    if let newTimecode { cachedTimecodes[gamePk] = newTimecode }
                }
            } else {
                // No cache - full fetch
                let (decoded, rawData, newTimecode) = try await mlbAPI.fetchLiveFeedRaw(gamePk: gamePk, label: label)
                feed = decoded
                cachedFeedData[gamePk] = rawData
                if let newTimecode { cachedTimecodes[gamePk] = newTimecode }
            }

            processFeed(feed, gamePk: gamePk, game: game)

            if feed.gameState == "Final" {
                let playerIDsInGame = rosterPlayerIDs.filter { id in
                    isPlayerInGame(playerID: id, game: game)
                }
                print("[GameMonitor] Game \(gamePk) is Final - marking done: \(playerIDsInGame)")
                stateManager?.setGameOver(playerIDs: Array(playerIDsInGame), gamePk: gamePk)
                stopMonitoring(gamePk: gamePk)
            }
        } catch {
            // Clear cache so next cycle does a full fetch
            cachedFeedData.removeValue(forKey: gamePk)
            cachedTimecodes.removeValue(forKey: gamePk)
            print("[GameMonitor] Error for game \(gamePk): \(error) - will full-fetch next cycle")
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

        let inning = formatInning(feed)
        let sharedFields = (
            gamePk: gamePk, inning: inning,
            homeTeam: homeShort, awayTeam: awayShort,
            homeTeamID: feed.homeTeamID, awayTeamID: feed.awayTeamID,
            homeScore: feed.homeScore, awayScore: feed.awayScore,
            balls: feed.balls, strikes: feed.strikes, outs: feed.outs,
            runnerOnFirst: feed.runnerOnFirst, runnerOnSecond: feed.runnerOnSecond, runnerOnThird: feed.runnerOnThird
        )

        func makeContext(role: PlayerState.ActiveRole) -> PlayerState.GameContext {
            PlayerState.GameContext(
                gamePk: sharedFields.gamePk, role: role, inning: sharedFields.inning,
                homeTeam: sharedFields.homeTeam, awayTeam: sharedFields.awayTeam,
                homeTeamID: sharedFields.homeTeamID, awayTeamID: sharedFields.awayTeamID,
                homeScore: sharedFields.homeScore, awayScore: sharedFields.awayScore,
                balls: sharedFields.balls, strikes: sharedFields.strikes, outs: sharedFields.outs,
                runnerOnFirst: sharedFields.runnerOnFirst, runnerOnSecond: sharedFields.runnerOnSecond,
                runnerOnThird: sharedFields.runnerOnThird
            )
        }

        // Check current batter - only track if rostered as hitter
        if let batterID = feed.currentBatterID {
            if let player = rosterPlayers[batterID], player.isHitter {
                let isNew = lastBatterID[gamePk] != batterID
                print("[GameMonitor] >>> ROSTER BATTER: \(feed.currentBatterName ?? "?") (ID \(batterID)) - \(isNew ? "NEW" : "same") - \(inning)")
                stateManager?.update(playerID: batterID, state: .active(makeContext(role: .batting)))
            } else if lastBatterID[gamePk] != batterID {
                if rosterPlayerIDs.contains(batterID) {
                    print("[GameMonitor] Batter \(feed.currentBatterName ?? "?") (ID \(batterID)) on roster as pitcher-only, skipping")
                } else {
                    print("[GameMonitor] Batter \(feed.currentBatterName ?? "?") (ID \(batterID)) not on roster")
                }
            }
        }

        // Check current pitcher - only track if rostered as pitcher
        if let pitcherID = feed.currentPitcherID {
            if let player = rosterPlayers[pitcherID], player.isPitcher {
                let isNew = lastPitcherID[gamePk] != pitcherID
                print("[GameMonitor] >>> ROSTER PITCHER: \(feed.currentPitcherName ?? "?") (ID \(pitcherID)) - \(isNew ? "NEW" : "same") - \(inning)")
                stateManager?.update(playerID: pitcherID, state: .active(makeContext(role: .pitching)))
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
