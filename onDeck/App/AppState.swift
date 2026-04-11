import SwiftUI

@Observable
@MainActor
final class AppState {
    // Player lists (derived from StateManager)
    var activePlayers: [Player] = []
    var inGamePlayers: [Player] = []     // game started, not at bat
    var upcomingPlayers: [Player] = []   // game hasn't started
    var donePlayers: [Player] = []       // game over or substituted out
    var games: [Game] = []

    // Managers
    let rosterManager = RosterManager()
    let scheduleManager = ScheduleManager()
    let gameMonitor = GameMonitor()
    let stateManager = StateManager()
    private let notificationManager = NotificationManager.shared
    private let fantraxAPI = FantraxAPI()

    // Sync state
    var isSyncing = false

    // Settings
    var rosterURL: String {
        get { UserDefaults.standard.string(forKey: "rosterURL") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "rosterURL") }
    }

    var selectedTeamID: String {
        get { UserDefaults.standard.string(forKey: "selectedTeamID") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "selectedTeamID") }
    }

    var hideBenchPlayers: Bool = UserDefaults.standard.bool(forKey: "hideBenchPlayers") {
        didSet {
            UserDefaults.standard.set(hideBenchPlayers, forKey: "hideBenchPlayers")
            updatePlayerLists()
        }
    }

    var alwaysOpenPopout: Bool = UserDefaults.standard.bool(forKey: "alwaysOpenPopout") {
        didSet { UserDefaults.standard.set(alwaysOpenPopout, forKey: "alwaysOpenPopout") }
    }

    // Team picker state
    var availableTeams: [FantraxAPI.FantraxTeam] = []
    var isLoadingTeams = false
    var teamsError: String?

    var menuBarTitle: String {
        let names = activePlayers.map(\.name)
        switch names.count {
        case 0: return ""
        case 1...3: return names.joined(separator: " | ")
        default: return names.prefix(3).joined(separator: " | ") + " +\(names.count - 3)"
        }
    }

    /// The parsed leagueID from the current URL, if valid.
    var parsedLeagueID: String? {
        FantraxURLParser.parse(rosterURL)?.leagueID
    }

    /// Whether the URL already contains a teamId (no picker needed).
    var urlHasTeamID: Bool {
        FantraxURLParser.parse(rosterURL)?.teamID != nil
    }

    /// The effective teamID to use - from URL if available, otherwise from picker.
    var effectiveTeamID: String? {
        if let parsed = FantraxURLParser.parse(rosterURL), let teamID = parsed.teamID {
            return teamID
        }
        return selectedTeamID.isEmpty ? nil : selectedTeamID
    }

    private var midnightTask: Task<Void, Never>?
    private var preGameRefreshTask: Task<Void, Never>?
    private var hasStarted = false
    private var notifiedNotInLineup: Set<Int> = []
    private var lastResumeTime: Date = .distantPast

    init() {
        gameMonitor.configure(stateManager: stateManager)
        setupStateChangeHandler()
        setupLineupUpdateHandler()
        setupGameStartHandler()
        setupSystemResumeHandler()
        Task {
            await start()
            if alwaysOpenPopout && !FloatingPanel.shared.isShowing {
                FloatingPanel.shared.toggle(appState: self)
            }
        }
    }

    // MARK: - Lifecycle

    func start() async {
        guard !hasStarted else { return }
        hasStarted = true

        _ = await notificationManager.requestPermission()

        guard !rosterURL.isEmpty else { return }

        // If URL has teamId, sync directly. Otherwise, fetch teams first.
        if let parsed = FantraxURLParser.parse(rosterURL) {
            if let teamID = parsed.teamID {
                await rosterManager.syncRoster(leagueID: parsed.leagueID, teamID: teamID)
            } else if !selectedTeamID.isEmpty {
                await rosterManager.syncRoster(leagueID: parsed.leagueID, teamID: selectedTeamID)
            } else {
                // No team selected yet - fetch teams so user can pick
                await fetchTeams()
                return
            }
        } else {
            return
        }

        await fetchScheduleAndStartMonitoring()
        scheduleDailyRefresh()
    }

    // MARK: - Team Fetching

    func fetchTeams() async {
        guard let leagueID = parsedLeagueID else {
            teamsError = "Invalid Fantrax URL"
            return
        }

        isLoadingTeams = true
        teamsError = nil

        do {
            availableTeams = try await fantraxAPI.fetchTeams(leagueID: leagueID)
            // If a team was previously selected and still exists, keep it
            if !selectedTeamID.isEmpty && !availableTeams.contains(where: { $0.id == selectedTeamID }) {
                selectedTeamID = ""
            }
        } catch {
            teamsError = "Couldn't load teams: \(error.localizedDescription)"
        }

        isLoadingTeams = false
    }

    /// Manually trigger a roster re-sync. Returns true on success.
    @discardableResult
    func resyncRoster() async -> Bool {
        guard let leagueID = parsedLeagueID,
              let teamID = effectiveTeamID else { return false }

        isSyncing = true
        await rosterManager.syncRoster(leagueID: leagueID, teamID: teamID)
        let success = rosterManager.error == nil
        await fetchScheduleAndStartMonitoring()
        isSyncing = false
        return success
    }

    private func fetchScheduleAndStartMonitoring() async {
        let teamNames = Set(rosterManager.players.map(\.team))
        await scheduleManager.fetchSchedule(for: teamNames)
        games = scheduleManager.todaysGames

        stateManager.reset()
        notifiedNotInLineup.removeAll()
        initializePlayerStates()

        gameMonitor.stopMonitoring()
        if !games.isEmpty {
            gameMonitor.startMonitoring(games: games, players: rosterManager.players)
            // Seed lineup data from schedule (available before live feed polling starts)
            for game in games {
                let lineup = GameLineup(home: Set(game.homeLineup), away: Set(game.awayLineup))
                if lineup.isSubmitted(for: .home) || lineup.isSubmitted(for: .away) {
                    gameMonitor.lineupPlayerIDs[game.id] = lineup
                    await reconcileLineupNotifications(gamePk: game.id)
                }
            }
            schedulePreGameRefresh()
        }
    }

    // MARK: - State Management

    private func setupStateChangeHandler() {
        stateManager.onStateChange = { [weak self] playerID, oldState, newState in
            guard let self else { return }
            self.updatePlayerLists()

            Task { @MainActor in
                await self.handleStateTransition(
                    playerID: playerID,
                    oldState: oldState,
                    newState: newState
                )
            }
        }
    }

    /// Fires a one-shot "not in lineup" notification for active-roster hitters
    /// whose team is playing in the given game but who are not in the posted lineup.
    private func reconcileLineupNotifications(gamePk: Int) async {
        guard let game = games.first(where: { $0.id == gamePk }),
              let lineup = gameMonitor.lineupPlayerIDs[gamePk] else { return }

        // Don't notify once the game has started - too late to act on a bench swap.
        // Prefer live feed state when available, otherwise fall back to scheduled start.
        if let feedState = gameMonitor.latestFeeds[gamePk]?.gameState,
           feedState == "Live" || feedState == "Final" {
            return
        }
        if gameMonitor.latestFeeds[gamePk] == nil, game.startTime <= Date.now {
            return
        }

        for player in rosterManager.players {
            guard player.isHitter,
                  player.rosterStatus == .active,
                  !notifiedNotInLineup.contains(player.id),
                  let side = game.side(for: player),
                  lineup.isSubmitted(for: side),
                  !lineup.ids(for: side).contains(player.id) else { continue }

            notifiedNotInLineup.insert(player.id)

            let matchup = "\(game.awayTeam) @ \(game.homeTeam)"
            let fantraxURL = rosterURL.isEmpty ? nil : URL(string: rosterURL)

            print("[Notification] NOT IN LINEUP: \(player.name) - \(matchup)")
            await notificationManager.notifyNotInLineup(
                playerName: player.name,
                playerID: player.id,
                gamePk: gamePk,
                game: matchup,
                fantraxURL: fantraxURL
            )
        }
    }

    /// Returns true if the player's team matches either side of the game
    /// (bidirectional substring match to handle team-name variants).
    private func isPlayerInGame(player: Player, game: Game) -> Bool {
        return game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
            || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
    }

    private func setupLineupUpdateHandler() {
        gameMonitor.onLineupUpdate = { [weak self] gamePk in
            Task { @MainActor in
                await self?.reconcileLineupNotifications(gamePk: gamePk)
            }
        }
    }

    private func setupGameStartHandler() {
        gameMonitor.onGameStart = { [weak self] gamePk in
            Task { @MainActor in
                await self?.notificationManager.purgeNotInLineupNotifications(gamePk: gamePk)
            }
        }
    }

    private func initializePlayerStates() {
        for game in games {
            let playersInGame = rosterManager.players.filter { player in
                game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
                    || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
            }
            stateManager.setUpcoming(
                playerIDs: playersInGame.map(\.id),
                startTime: game.startTime
            )
        }

        // Mark SP-only players as day off if they're not today's probable pitcher
        let probablePitcherIDs = Set(games.compactMap(\.homeProbablePitcherID) + games.compactMap(\.awayProbablePitcherID))
        for player in rosterManager.players where player.isStartingPitcherOnly && !probablePitcherIDs.contains(player.id) {
            stateManager.update(playerID: player.id, state: .inactive(reason: .dayOff))
        }

        let allGamePlayerIDs = Set(stateManager.playerStates.keys)
        for player in rosterManager.players where !allGamePlayerIDs.contains(player.id) {
            stateManager.update(playerID: player.id, state: .inactive(reason: .dayOff))
        }

        updatePlayerLists()
    }

    private func updatePlayerLists() {
        var active: [Player] = []
        var inGame: [Player] = []
        var upcoming: [Player] = []
        var done: [Player] = []

        let now = Date.now
        for player in rosterManager.players {
            if hideBenchPlayers && player.isOnBench { continue }
            switch stateManager.playerStates[player.id] {
            case .active:
                active.append(player)
            case .upcoming(let startTime):
                if startTime < now {
                    inGame.append(player)
                } else {
                    upcoming.append(player)
                }
            case .inactive(let reason):
                switch reason {
                case .gameOver(let gamePk), .substituted(let gamePk):
                    if hasStatLine(player: player, gamePk: gamePk) {
                        done.append(player)
                    }
                case .dayOff:
                    break
                }
            case .none:
                break
            }
        }

        activePlayers = active
        inGamePlayers = inGame
        upcomingPlayers = upcoming.sorted { a, b in
            let timeA = stateManager.startTime(for: a.id) ?? .distantFuture
            let timeB = stateManager.startTime(for: b.id) ?? .distantFuture
            if timeA != timeB { return timeA < timeB }
            return a.name < b.name
        }
        donePlayers = done.sorted { a, b in
            if a.isHitter != b.isHitter { return a.isHitter }
            return false
        }
    }

    private func hasStatLine(player: Player, gamePk: Int) -> Bool {
        guard let feed = gameMonitor.latestFeeds[gamePk],
              let stats = feed.playerStats[player.id] else { return false }
        if player.isPitcher && !player.isHitter {
            return stats.pitchingLine != nil
        }
        return stats.battingLine != nil
    }

    // MARK: - Notifications

    private func isStillActive(playerID: Int, role: PlayerState.ActiveRole) -> Bool {
        guard case .active(let ctx) = stateManager.playerStates[playerID] else { return false }
        return ctx.role == role
    }

    private func handleStateTransition(playerID: Int, oldState: PlayerState?, newState: PlayerState) async {
        guard let player = rosterManager.players.first(where: { $0.id == playerID }) else { return }
        if hideBenchPlayers && player.isOnBench { return }
        print("[Transition] \(player.name) (\(playerID)): \(String(describing: oldState)) -> \(String(describing: newState))")

        switch (oldState, newState) {
        case (_, .active(let context)):
            let wasActive: Bool
            if case .active = oldState { wasActive = true } else { wasActive = false }

            if !wasActive {
                let gameString = formatGameString(context: context)
                let streamURL = streamURL(for: context.gamePk)
                switch context.role {
                case .pitching:
                    print("[Notification] PITCHING: \(player.name) - \(gameString), \(context.inning)")
                    await notificationManager.notifyPitching(
                        playerName: player.name,
                        playerID: player.id,
                        gamePk: context.gamePk,
                        game: gameString,
                        inning: context.inning,
                        streamURL: streamURL
                    )
                    // Race guard: state may have changed during the async send
                    if !isStillActive(playerID: playerID, role: .pitching) {
                        print("[Notification] RACE GUARD purge pitching: \(player.name) (state changed during send)")
                        notificationManager.purgePitching(gamePk: context.gamePk, playerID: playerID)
                    }
                case .batting:
                    print("[Notification] BATTING: \(player.name) - \(gameString), \(context.inning)")
                    await notificationManager.notifyBatting(
                        playerName: player.name,
                        playerID: player.id,
                        gamePk: context.gamePk,
                        game: gameString,
                        inning: context.inning,
                        streamURL: streamURL
                    )
                    // Race guard: state may have changed during the async send
                    if !isStillActive(playerID: playerID, role: .batting) {
                        print("[Notification] RACE GUARD purge batting: \(player.name) (state changed during send)")
                        notificationManager.purgeBatting(gamePk: context.gamePk, playerID: playerID)
                    }
                }
            }

        case (.active(let context), .upcoming):
            switch context.role {
            case .batting:
                notificationManager.purgeBatting(gamePk: context.gamePk, playerID: playerID)
                if let lastFeedResult = gameMonitor.lastPlayDescriptions[playerID] {
                    await notificationManager.notifyAtBatResult(
                        playerName: player.name,
                        playerID: player.id,
                        description: lastFeedResult,
                        streamURL: streamURL(for: context.gamePk)
                    )
                }
            case .pitching:
                notificationManager.purgePitching(gamePk: context.gamePk, playerID: playerID)
            }

        case (.active(let context), .inactive(.substituted(_))):
            if context.role == .pitching {
                notificationManager.purgePitching(gamePk: context.gamePk, playerID: playerID)
                await notificationManager.notifyPitchingResult(
                    playerName: player.name,
                    playerID: player.id,
                    description: "\(player.name) has been pulled from the game",
                    streamURL: streamURL(for: context.gamePk)
                )
            }

        case (.active(let context), .inactive(.gameOver(_))):
            switch context.role {
            case .batting:
                notificationManager.purgeBatting(gamePk: context.gamePk, playerID: playerID)
            case .pitching:
                notificationManager.purgePitching(gamePk: context.gamePk, playerID: playerID)
            }

        default:
            break
        }
    }

    private func streamURL(for gamePk: Int) -> URL? {
        guard let game = games.first(where: { $0.id == gamePk }) else { return nil }
        return StreamLinkRouter.url(for: game)
    }

    private func formatGameString(context: PlayerState.GameContext) -> String {
        return "\(context.awayTeam) \(context.awayScore) - \(context.homeTeam) \(context.homeScore)"
    }

    // MARK: - Pre-Game Refresh (15 min before first game)

    private func schedulePreGameRefresh() {
        preGameRefreshTask?.cancel()

        guard let earliestStart = games.map(\.startTime).min() else { return }
        let refreshTime = earliestStart.addingTimeInterval(-15 * 60)
        let delay = refreshTime.timeIntervalSinceNow

        // If already past the refresh window, skip - monitoring is already running
        if delay <= 0 {
            print("[AppState] Pre-game refresh: skipping (first game at \(earliestStart) already started)")
            return
        }

        print("[AppState] Pre-game refresh scheduled for \(refreshTime) (15 min before \(earliestStart))")
        preGameRefreshTask = Task { [weak self] in
            do {
                try await Task.sleep(for: .seconds(delay), tolerance: .seconds(60))
            } catch {
                return // Task cancelled
            }
            print("[AppState] Pre-game refresh firing")
            await self?.resyncRoster()
        }
    }

    // MARK: - Sleep/Wake & Unlock Recovery

    private func setupSystemResumeHandler() {
        print("[AppState] Registering system resume observers")
        let center = NSWorkspace.shared.notificationCenter
        let handler: (Notification) -> Void = { [weak self] notification in
            guard let self else { return }
            print("[AppState] System resume: \(notification.name.rawValue)")
            Task { @MainActor in
                await self.handleSystemResume()
            }
        }
        center.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main, using: handler)
        center.addObserver(forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: .main, using: handler)
        DistributedNotificationCenter.default().addObserver(forName: NSNotification.Name("com.apple.screenIsUnlocked"), object: nil, queue: .main, using: handler)
        print("[AppState] System resume observers registered (wake, session, screen unlock)")
    }

    private func handleSystemResume() async {
        // Debounce: skip if recovery ran within the last 30 seconds
        let now = Date()
        guard now.timeIntervalSince(lastResumeTime) > 30 else {
            print("[AppState] Resume debounced (already recovered recently)")
            return
        }
        lastResumeTime = now

        guard !rosterURL.isEmpty, effectiveTeamID != nil else { return }

        print("[AppState] Recovering from system resume - clearing caches and resyncing")
        gameMonitor.clearCaches()
        await resyncRoster()
    }

    // MARK: - Daily Refresh (8 AM)

    private func scheduleDailyRefresh() {
        midnightTask?.cancel()
        midnightTask = Task { [weak self] in
            while !Task.isCancelled {
                let now = Date()
                let calendar = Calendar.current
                let hour = calendar.component(.hour, from: now)

                // Calculate next 8 AM
                let next8AM: Date
                if hour < 8 {
                    // Before 8 AM today - next refresh is 8 AM today
                    next8AM = calendar.date(bySettingHour: 8, minute: 0, second: 0, of: now) ?? now
                } else {
                    // After 8 AM - next refresh is 8 AM tomorrow
                    guard let tomorrow = calendar.date(byAdding: .day, value: 1, to: now) else { return }
                    next8AM = calendar.date(bySettingHour: 8, minute: 0, second: 0, of: tomorrow) ?? tomorrow
                }

                let interval = next8AM.timeIntervalSince(now)
                do {
                    try await Task.sleep(for: .seconds(max(interval, 60)), tolerance: .seconds(120))
                } catch {
                    return
                }
                await self?.resyncRoster()
            }
        }
    }
}
