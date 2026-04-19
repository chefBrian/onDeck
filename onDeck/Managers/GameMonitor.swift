import Foundation
import os

private let log = Logger(subsystem: "dev.bjc.onDeck", category: "GameMonitor")

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

    /// Pre-game milestone times (seconds before game start) for one-shot lineup checks.
    private static let preGameMilestones: [TimeInterval] = [2 * 3600, 1 * 3600, 30 * 60]

    /// Tracks which pre-game milestones have been fetched per game.
    private var completedMilestones: [Int: Set<TimeInterval>] = [:] // gamePk -> milestone intervals

    /// Stores the last completed play description per player (for result notifications).
    var lastPlayDescriptions: [Int: String] = [:] // playerID -> description

    /// Latest feed data per game (for In Game player display).
    var latestFeeds: [Int: LiveFeedData] = [:] // gamePk -> feed

    /// Lineup player IDs per game, tracked per side so consumers can tell
    /// whether a player's own team has submitted yet (vs just the opponent).
    var lineupPlayerIDs: [Int: GameLineup] = [:] // gamePk -> per-side lineup IDs

    /// Games that have been observed in Live/In Progress at least once (for one-shot start detection).
    private var liveGamesSeen: Set<Int> = []

    /// Callback fired when `lineupPlayerIDs[gamePk]` is populated or changes.
    var onLineupUpdate: ((Int) -> Void)?

    /// Callback fired once per game the first time it transitions to Live/In Progress.
    var onGameStart: ((Int) -> Void)?

    /// Whether the feed has observed this game as Live/In Progress. Driven by the feed, not the clock,
    /// so late-starting games aren't misclassified.
    func isLive(gamePk: Int) -> Bool {
        liveGamesSeen.contains(gamePk)
    }

    func configure(stateManager: StateManager) {
        self.stateManager = stateManager
    }

    func startMonitoring(games: [Game], players: [Player]) {
        stopMonitoring()

        rosterPlayerIDs = Set(players.map(\.id))
        rosterPlayers = Dictionary(uniqueKeysWithValues: players.map { ($0.id, $0) })
        monitoredGames = Dictionary(uniqueKeysWithValues: games.map { ($0.id, $0) })
        isMonitoring = true

        log.info("[GameMonitor] Starting monitoring for \(games.count) games")
        log.debug("[GameMonitor] Watching \(self.rosterPlayerIDs.count) roster player IDs: \(self.rosterPlayerIDs.sorted(), privacy: .public)")
        for player in players {
            log.debug("[GameMonitor]   \(player.id) = \(player.name, privacy: .public) (\(player.team, privacy: .public))")
        }

        for game in games {
            log.info("[GameMonitor] Monitoring game \(game.id): \(game.awayTeam, privacy: .public) @ \(game.homeTeam, privacy: .public)")
        }

        coordinatorTask = Task { [weak self] in
            await self?.coordinatePolling()
        }
    }

    func stopMonitoring() {
        let wasMonitoring = isMonitoring
        coordinatorTask?.cancel()
        coordinatorTask = nil
        monitoredGames.removeAll()
        lastBatterID.removeAll()
        lastPitcherID.removeAll()
        lastHomePitcherID.removeAll()
        lastAwayPitcherID.removeAll()
        lineupPlayerIDs.removeAll()
        liveGamesSeen.removeAll()
        completedMilestones.removeAll()
        // Full stop (e.g. midnight refresh) drops latestFeeds. The per-game
        // stopMonitoring(gamePk:) intentionally retains latestFeeds so the
        // Done section can keep reading stats for finished games.
        latestFeeds.removeAll()
        lastPlayDescriptions.removeAll()
        isMonitoring = false
        // startMonitoring() calls stopMonitoring() as part of its reset; skip the
        // pressure relief there since we're about to allocate for new work.
        if wasMonitoring {
            MemoryPressureRelief.releaseReclaimablePages()
        }
    }

    /// Nulls each cached feed's timeStamp so the next poll cycle does a full
    /// fetch per game. Used after system wake when stored timecodes are stale.
    /// Preserves the rest of each `LiveFeedData` so the UI keeps rendering
    /// last-known state during the round trip.
    func invalidateTimecodes() {
        for key in latestFeeds.keys {
            latestFeeds[key]?.timeStamp = nil
        }
        log.info("[GameMonitor] Timecodes invalidated (stale - next poll does full fetch per game)")
    }

    /// Stops monitoring a specific game (e.g., when no roster players remain).
    func stopMonitoring(gamePk: Int) {
        monitoredGames.removeValue(forKey: gamePk)
        lineupPlayerIDs.removeValue(forKey: gamePk)
        lastBatterID.removeValue(forKey: gamePk)
        lastPitcherID.removeValue(forKey: gamePk)
        lastHomePitcherID.removeValue(forKey: gamePk)
        lastAwayPitcherID.removeValue(forKey: gamePk)
        completedMilestones.removeValue(forKey: gamePk)
        liveGamesSeen.remove(gamePk)
        // Keep latestFeeds[gamePk] - AppState's Done section reads feed.playerStats for finished games.
        if monitoredGames.isEmpty {
            coordinatorTask?.cancel()
            coordinatorTask = nil
            isMonitoring = false
            MemoryPressureRelief.releaseReclaimablePages()
        }
    }

    // MARK: - Centralized Polling

    private func coordinatePolling() async {
        while !Task.isCancelled {
            let sleepDuration = nextEventDelay()
            if sleepDuration > 0 {
                log.debug("[GameMonitor] Sleeping \(Int(sleepDuration))s until next event")
                do {
                    try await Task.sleep(for: .seconds(sleepDuration), tolerance: .seconds(sleepDuration > 60 ? 30 : 2))
                } catch {
                    return
                }
            }

            await pollCycle()

            // Once any game is in active polling range, switch to 10s loop
            let hasActiveGames = monitoredGames.values.contains {
                $0.startTime.addingTimeInterval(-15 * 60) <= Date.now
            }
            if hasActiveGames {
                do {
                    try await Task.sleep(for: .seconds(10), tolerance: .seconds(2))
                } catch {
                    return
                }
            }
        }
    }

    private func pollCycle() async {
        var gamesToPoll: Set<Int> = []

        // Active games: within 15 min of start - poll every cycle
        for (gamePk, game) in monitoredGames {
            if game.startTime.addingTimeInterval(-15 * 60) <= Date.now {
                gamesToPoll.insert(gamePk)
            }
        }

        // Pre-game milestone checks: one-shot fetch when a milestone is reached
        for (gamePk, game) in monitoredGames {
            let timeUntilStart = game.startTime.timeIntervalSinceNow
            guard timeUntilStart > 15 * 60 else { continue } // Already active

            for milestone in Self.preGameMilestones {
                if timeUntilStart <= milestone,
                   !(completedMilestones[gamePk]?.contains(milestone) ?? false) {
                    completedMilestones[gamePk, default: []].insert(milestone)
                    gamesToPoll.insert(gamePk)
                    log.info("[GameMonitor] Pre-game lineup check for game \(gamePk) (\(Int(milestone / 60))min milestone)")
                    break
                }
            }
        }

        guard !gamesToPoll.isEmpty else { return }

        await withTaskGroup(of: Void.self) { group in
            for gamePk in gamesToPoll {
                guard let game = monitoredGames[gamePk] else { continue }
                group.addTask { [weak self] in
                    guard let self else { return }
                    await self.pollSingleGame(gamePk: gamePk, game: game)
                }
            }
        }
    }

    private func pollSingleGame(gamePk: Int, game: Game) async {
        let label = "\(TeamMapping.abbreviation(for: game.awayTeam))@\(TeamMapping.abbreviation(for: game.homeTeam))"
        do {
            let feed: LiveFeedData

            if let existing = latestFeeds[gamePk], let timecode = existing.timeStamp {
                let result = try await mlbAPI.fetchDiffPatch(gamePk: gamePk, since: timecode, label: label)

                switch result {
                case .noChanges:
                    return

                case .patches(let patches):
                    var working = existing
                    LiveFeedPatcher.apply(patches, to: &working)
                    latestFeeds[gamePk] = working
                    feed = working

                case .fullUpdate(let rawData):
                    let decoded = try MLBStatsAPI.decodeLiveFeed(from: rawData)
                    latestFeeds[gamePk] = decoded
                    feed = decoded
                }
            } else {
                // No seed - full fetch
                let decoded = try await mlbAPI.fetchLiveFeedRaw(gamePk: gamePk, label: label)
                latestFeeds[gamePk] = decoded
                feed = decoded
            }

            processFeed(feed, gamePk: gamePk, game: game)

            if feed.gameState == "Final" {
                // Postponed carries gameState "Final" but has no stats - marking players
                // .gameOver would filter them out of the UI entirely (Done section
                // requires a statLine). Leave them in .upcoming so the UPCOMING row's
                // red X icon + "PPD" label stays visible until the next day's refresh.
                if feed.detailedState == "Postponed" {
                    log.notice("[GameMonitor] Game \(gamePk) Postponed - retaining .upcoming state, stopping poll")
                    stopMonitoring(gamePk: gamePk)
                } else {
                    let playerIDsInGame = rosterPlayerIDs.filter { id in
                        isPlayerInGame(playerID: id, game: game)
                    }
                    log.notice("[GameMonitor] Game \(gamePk) is Final - marking done: \(playerIDsInGame, privacy: .public)")
                    stateManager?.setGameOver(playerIDs: Array(playerIDsInGame), gamePk: gamePk)
                    stopMonitoring(gamePk: gamePk)
                }
            }
        } catch {
            // Transient error - preserve last-known feed for UI continuity, but
            // null its timeStamp so the next cycle does a full fetch.
            latestFeeds[gamePk]?.timeStamp = nil
            log.error("[GameMonitor] Error for game \(gamePk): \(error, privacy: .public) - will full-fetch next cycle")
        }
    }

    // MARK: - Feed Processing

    private func processFeed(_ feed: LiveFeedData, gamePk: Int, game: Game) {
        // Track lineup per side. Only overwrite a batting side when the feed
        // actually has data for it - an empty side means that team hasn't
        // filed its lineup card yet, not that we should drop what we had.
        // Pitchers live in a separate set so that a submitted batting card
        // (used to gate "not in lineup" logic) doesn't falsely flag the
        // probable starter as missing before the boxscore lists him.
        let homeBatters = Set(feed.homeBattingOrder)
        let awayBatters = Set(feed.awayBattingOrder)
        let homePitchers = Set(feed.homePitchers + [game.homeProbablePitcherID].compactMap { $0 })
        let awayPitchers = Set(feed.awayPitchers + [game.awayProbablePitcherID].compactMap { $0 })
        let existing = lineupPlayerIDs[gamePk] ?? GameLineup()
        let updated = GameLineup(
            home: homeBatters.isEmpty ? existing.home : homeBatters,
            away: awayBatters.isEmpty ? existing.away : awayBatters,
            homePitchers: homePitchers.isEmpty ? existing.homePitchers : homePitchers,
            awayPitchers: awayPitchers.isEmpty ? existing.awayPitchers : awayPitchers
        )
        if updated != existing {
            lineupPlayerIDs[gamePk] = updated
            onLineupUpdate?(gamePk)
        }

        // Allowlist of detailedStates that count as "ball in play or paused mid-play".
        // abstractGameState "Live" alone isn't enough - it also covers "Warmup" (~30 min
        // pre-first-pitch) and briefly "Game Over" before the flip to Final. Pre-game
        // "Delayed Start: Rain" carries abstractGameState "Preview", so hasPrefix("Delayed")
        // here only matches the mid-game "Delayed: Rain" form - don't tighten it without
        // re-checking, or rain delay detection breaks.
        let detailed = feed.detailedState ?? ""
        let isPlayable = detailed == "In Progress"
                      || detailed.hasPrefix("Delayed")
                      || detailed.hasPrefix("Suspended")
                      || detailed == "Manager challenge"
        guard feed.gameState == "Live", isPlayable else {
            log.debug("[GameMonitor] Game \(gamePk) state: \(feed.gameState, privacy: .public)/\(feed.detailedState ?? "nil", privacy: .public) (skipping)")
            return
        }

        if liveGamesSeen.insert(gamePk).inserted {
            onGameStart?(gamePk)
        }

        // Between half-innings, currentBatter/currentPitcher are stale holdover from the
        // last play of the previous half-inning - MLB doesn't clear them until play resumes.
        // Mid-game delays (rain etc.) also pause play, so flip active players out - the
        // "Active Now" section should only hold players whose at-bat/inning is live.
        let isInProgress = feed.detailedState == "In Progress"
        let isBreak = !isInProgress || feed.inningState == "Middle" || feed.inningState == "End"

        let awayShort = game.awayTeam.split(separator: " ").last.map(String.init) ?? game.awayTeam
        let homeShort = game.homeTeam.split(separator: " ").last.map(String.init) ?? game.homeTeam

        let inning = formatInning(feed)
        let sharedFields = (
            gamePk: gamePk, inning: inning,
            homeTeam: homeShort, awayTeam: awayShort,
            homeTeamID: feed.homeTeamID, awayTeamID: feed.awayTeamID,
            homeScore: feed.homeScore, awayScore: feed.awayScore,
            balls: feed.balls, strikes: feed.strikes, outs: feed.outs,
            runnerOnFirst: feed.runnerOnFirst != nil, runnerOnSecond: feed.runnerOnSecond != nil, runnerOnThird: feed.runnerOnThird != nil
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

        if isBreak {
            // Flip any roster player currently active in this game to upcoming.
            // Leaves substituted players alone (they're .inactive, not .active).
            for id in rosterPlayerIDs {
                guard let state = stateManager?.playerStates[id],
                      case .active(let ctx) = state,
                      ctx.gamePk == gamePk else { continue }
                stateManager?.update(playerID: id, state: .upcoming(startTime: game.startTime))
            }
        } else {
            // Check current batter - only track if rostered as hitter
            if let batterID = feed.currentBatterID {
                if let player = rosterPlayers[batterID], player.isHitter {
                    let isNew = lastBatterID[gamePk] != batterID
                    log.info("[GameMonitor] >>> ROSTER BATTER: \(feed.currentBatterName ?? "?", privacy: .public) (ID \(batterID)) - \(isNew ? "NEW" : "same", privacy: .public) - \(inning, privacy: .public)")
                    stateManager?.update(playerID: batterID, state: .active(makeContext(role: .batting)))
                } else if lastBatterID[gamePk] != batterID {
                    if rosterPlayerIDs.contains(batterID) {
                        log.debug("[GameMonitor] Batter \(feed.currentBatterName ?? "?", privacy: .public) (ID \(batterID)) on roster as pitcher-only, skipping")
                    } else {
                        log.debug("[GameMonitor] Batter \(feed.currentBatterName ?? "?", privacy: .public) (ID \(batterID)) not on roster")
                    }
                }
            }

            // Check current pitcher - only track if rostered as pitcher
            if let pitcherID = feed.currentPitcherID {
                if let player = rosterPlayers[pitcherID], player.isPitcher {
                    let isNew = lastPitcherID[gamePk] != pitcherID
                    log.info("[GameMonitor] >>> ROSTER PITCHER: \(feed.currentPitcherName ?? "?", privacy: .public) (ID \(pitcherID)) - \(isNew ? "NEW" : "same", privacy: .public) - \(inning, privacy: .public)")
                    stateManager?.update(playerID: pitcherID, state: .active(makeContext(role: .pitching)))
                }
            }
        }

        // Check if previous batter from our roster is no longer active.
        // Only revert if they were actually a hitter - pitcher-only roster players
        // (e.g. Ohtani-P) can appear as the feed's current batter without being tracked.
        if let prevBatter = lastBatterID[gamePk],
           prevBatter != feed.currentBatterID,
           let prevPlayer = rosterPlayers[prevBatter],
           prevPlayer.isHitter {
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

        // Revert pitcher to in-game when half-inning changes (they're not on the mound)
        if let prevPitcher = lastPitcherID[gamePk],
           prevPitcher != feed.currentPitcherID,
           rosterPlayerIDs.contains(prevPitcher) {
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
                      feed.playerStats[id]?.pitching?.formatted != nil,
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

    /// Returns seconds until the next event (milestone or active polling window).
    /// Returns 0 if an event is ready now.
    private func nextEventDelay() -> TimeInterval {
        var nextTime: Date?

        for game in monitoredGames.values {
            // Active polling starts 15 min before game
            let activeStart = game.startTime.addingTimeInterval(-15 * 60)
            if activeStart <= Date.now { return 0 }

            // Check uncompleted milestones
            let completed = completedMilestones[game.id] ?? []
            for milestone in Self.preGameMilestones {
                let milestoneTime = game.startTime.addingTimeInterval(-milestone)
                if milestoneTime <= Date.now && !completed.contains(milestone) { return 0 }
                if milestoneTime > Date.now {
                    if nextTime == nil || milestoneTime < nextTime! {
                        nextTime = milestoneTime
                    }
                }
            }

            if nextTime == nil || activeStart < nextTime! {
                nextTime = activeStart
            }
        }

        guard let next = nextTime else { return 0 }
        return max(0, next.timeIntervalSinceNow)
    }

    private func formatInning(_ feed: LiveFeedData) -> String {
        guard let inning = feed.inning, let half = feed.inningHalf else { return "" }
        let shortHalf = half == "Top" ? "Top" : "Bot"
        return "\(shortHalf) \(inning)"
    }
}
