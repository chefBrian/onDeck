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
    private let fantraxAPI = FantraxAPI()

    // Settings
    var rosterURL: String {
        get { UserDefaults.standard.string(forKey: "rosterURL") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "rosterURL") }
    }

    var selectedTeamID: String {
        get { UserDefaults.standard.string(forKey: "selectedTeamID") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "selectedTeamID") }
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

    init() {
        gameMonitor.configure(stateManager: stateManager)
        setupStateChangeHandler()
    }

    // MARK: - Lifecycle

    func start() async {
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
        scheduleMidnightRefresh()
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

    /// Manually trigger a roster re-sync.
    func resyncRoster() async {
        guard let leagueID = parsedLeagueID,
              let teamID = effectiveTeamID else { return }

        await rosterManager.syncRoster(leagueID: leagueID, teamID: teamID)
        await fetchScheduleAndStartMonitoring()
    }

    private func fetchScheduleAndStartMonitoring() async {
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
            let wasActive: Bool
            if case .active = oldState { wasActive = true } else { wasActive = false }

            if !wasActive {
                let gameString = formatGameString(gamePk: context.gamePk)
                if player.isPitcher && !player.isHitter {
                    print("[Notification] PITCHING: \(player.name) - \(gameString), \(context.inning)")
                    await notificationManager.notifyPitching(
                        playerName: player.name,
                        game: gameString,
                        inning: context.inning
                    )
                } else {
                    print("[Notification] BATTING: \(player.name) - \(gameString), \(context.inning)")
                    await notificationManager.notifyBatting(
                        playerName: player.name,
                        game: gameString,
                        inning: context.inning
                    )
                }
            }

        case (.active, .upcoming):
            if let lastFeedResult = gameMonitor.lastPlayDescriptions[playerID] {
                if player.isHitter {
                    await notificationManager.notifyAtBatResult(
                        playerName: player.name,
                        description: lastFeedResult
                    )
                }
            }

        case (.active, .inactive(.substituted)):
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
                await resyncRoster()
            }
        }
    }
}
