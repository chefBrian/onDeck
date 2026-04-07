import SwiftUI

@Observable
@MainActor
final class AppState {
    // Player lists (derived from StateManager)
    var activePlayers: [Player] = []
    var upcomingPlayers: [Player] = []
    var inactivePlayers: [Player] = []
    var games: [Game] = []

    // Managers
    let rosterManager = RosterManager()
    let scheduleManager = ScheduleManager()
    let gameMonitor = GameMonitor()
    let stateManager = StateManager()
    private let notificationManager = NotificationManager.shared

    // Settings
    var rosterURL: String {
        get { UserDefaults.standard.string(forKey: "rosterURL") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "rosterURL") }
    }

    var menuBarTitle: String {
        let names = activePlayers.map(\.name)
        switch names.count {
        case 0: return ""
        case 1...3: return names.joined(separator: " | ")
        default: return names.prefix(3).joined(separator: " | ") + " +\(names.count - 3)"
        }
    }

    private var midnightTask: Task<Void, Never>?

    init() {
        gameMonitor.configure(stateManager: stateManager)
        setupStateChangeHandler()
    }

    // MARK: - Lifecycle

    func start() async {
        // Request notification permission
        _ = await notificationManager.requestPermission()

        // Sync roster if URL is set
        guard !rosterURL.isEmpty else { return }
        await rosterManager.syncRoster(from: rosterURL)

        // Fetch today's schedule
        let teamNames = Set(rosterManager.players.map(\.team))
        await scheduleManager.fetchSchedule(for: teamNames)
        games = scheduleManager.todaysGames

        // Set initial player states
        initializePlayerStates()

        // Start monitoring live games
        if !games.isEmpty {
            gameMonitor.startMonitoring(games: games, players: rosterManager.players)
        }

        // Schedule midnight refresh
        scheduleMidnightRefresh()
    }

    /// Manually trigger a roster re-sync.
    func resyncRoster() async {
        guard !rosterURL.isEmpty else { return }
        await rosterManager.syncRoster(from: rosterURL)

        let teamNames = Set(rosterManager.players.map(\.team))
        await scheduleManager.fetchSchedule(for: teamNames)
        games = scheduleManager.todaysGames

        stateManager.reset()
        initializePlayerStates()

        gameMonitor.stopMonitoring()
        if !games.isEmpty {
            gameMonitor.startMonitoring(games: games, players: rosterManager.players)
        }
    }

    // MARK: - State Management

    private func setupStateChangeHandler() {
        stateManager.onStateChange = { [weak self] playerID, oldState, newState in
            guard let self else { return }
            self.updatePlayerLists()

            // Trigger notifications based on state transitions
            Task { @MainActor in
                await self.handleStateTransition(
                    playerID: playerID,
                    oldState: oldState,
                    newState: newState
                )
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

        // Players without games today
        let allGamePlayerIDs = Set(stateManager.playerStates.keys)
        for player in rosterManager.players where !allGamePlayerIDs.contains(player.id) {
            stateManager.update(playerID: player.id, state: .inactive(reason: .dayOff))
        }

        updatePlayerLists()
    }

    private func updatePlayerLists() {
        var active: [Player] = []
        var upcoming: [Player] = []
        var inactive: [Player] = []

        for player in rosterManager.players {
            switch stateManager.playerStates[player.id] {
            case .active:
                active.append(player)
            case .upcoming:
                upcoming.append(player)
            case .inactive, .none:
                inactive.append(player)
            }
        }

        activePlayers = active
        upcomingPlayers = upcoming
        inactivePlayers = inactive
    }

    // MARK: - Notifications

    private func handleStateTransition(playerID: Int, oldState: PlayerState?, newState: PlayerState) async {
        guard let player = rosterManager.players.first(where: { $0.id == playerID }) else { return }

        switch (oldState, newState) {
        case (_, .active(let context)):
            // Player became active - notify based on position
            let wasActive: Bool
            if case .active = oldState { wasActive = true } else { wasActive = false }

            if !wasActive {
                let gameString = formatGameString(gamePk: context.gamePk)
                if player.isPitcher && !player.isHitter {
                    await notificationManager.notifyPitching(
                        playerName: player.name,
                        game: gameString,
                        inning: context.inning
                    )
                } else {
                    await notificationManager.notifyBatting(
                        playerName: player.name,
                        game: gameString,
                        inning: context.inning
                    )
                }
            }

        case (.active, .upcoming):
            // Batter's at-bat finished - check for result notification
            if let lastFeedResult = gameMonitor.lastPlayDescriptions[playerID] {
                if player.isHitter {
                    await notificationManager.notifyAtBatResult(
                        playerName: player.name,
                        description: lastFeedResult
                    )
                }
            }

        case (.active, .inactive(.substituted)):
            // Player was pulled
            if player.isPitcher {
                await notificationManager.notifyPitchingResult(
                    playerName: player.name,
                    description: "\(player.name) has been pulled from the game"
                )
            }

        default:
            break
        }
    }

    private func formatGameString(gamePk: Int) -> String {
        guard let game = games.first(where: { $0.id == gamePk }) else { return "" }
        let away = game.awayTeam.split(separator: " ").last.map(String.init) ?? game.awayTeam
        let home = game.homeTeam.split(separator: " ").last.map(String.init) ?? game.homeTeam
        return "\(away) vs \(home)"
    }

    // MARK: - Midnight Refresh

    private func scheduleMidnightRefresh() {
        midnightTask?.cancel()
        midnightTask = Task {
            while !Task.isCancelled {
                let now = Date()
                let calendar = Calendar.current
                guard let tomorrow = calendar.date(byAdding: .day, value: 1, to: now),
                      let midnight = calendar.date(bySettingHour: 0, minute: 0, second: 0, of: tomorrow) else {
                    return
                }
                let interval = midnight.timeIntervalSince(now)
                do {
                    try await Task.sleep(for: .seconds(max(interval, 60)))
                } catch {
                    return
                }
                // Day rolled over - refresh everything
                await resyncRoster()
            }
        }
    }
}
