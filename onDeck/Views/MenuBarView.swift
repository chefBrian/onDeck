import SwiftUI

private enum BattingProximity {
    case atBat
    case onDeck
    case dueUp
    case order(Int)             // distance from current batter, 3...8
    case notBatting(spot: Int)  // other team is up; sort by lineup spot, below all live proximity

    /// Distance-based when the team is batting (0 = at bat, 8 = just finished),
    /// so the player who just batted sinks and bubbles back up as lineup cycles.
    /// notBatting bumps into a separate band so a leadoff hitter on a non-batting
    /// team doesn't tie with .onDeck.
    var sortKey: Int {
        switch self {
        case .atBat: 0
        case .onDeck: 1
        case .dueUp: 2
        case .order(let n): n
        case .notBatting(let spot): 50 + spot
        }
    }
}

/// Stacks tiers on top of the proximity sort: 0 = normal proximity (distance-based
/// so just-batted sinks, on-deck bubbles up), +100 = mid-game delay, +200 =
/// lineup card filed without this player. Pitchers have nil proximity - they get
/// a base of 0 if currently pitching (live action, like .atBat) or 70 otherwise
/// (above .notBatting hitters, below the delay tier).
private func inGameSortKey(for player: Player, proximity: BattingProximity?, in appState: AppState) -> Int {
    guard let game = appState.games.first(where: { game in
        game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
            || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
    }) else { return proximity?.sortKey ?? 70 }

    let feed = appState.gameMonitor.latestFeeds[game.id]
    let base: Int = {
        if let key = proximity?.sortKey { return key }
        return feed?.currentPitcherID == player.id ? 0 : 70
    }()

    // Not in Lineup: own side's card is filed and this player isn't on it.
    if let side = game.side(for: player),
       let lineup = appState.gameMonitor.lineupPlayerIDs[game.id],
       lineup.isSubmitted(for: side),
       !lineup.ids(for: side).contains(player.id) {
        return 200 + base
    }

    if let detailed = feed?.detailedState,
       detailed.hasPrefix("Delayed") || detailed.hasPrefix("Suspended") {
        return 100 + base
    }

    return base
}

private func battingProximity(for player: Player, in appState: AppState) -> BattingProximity? {
    guard let game = appState.games.first(where: { game in
        game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
            || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
    }),
        let feed = appState.gameMonitor.latestFeeds[game.id] else { return nil }
    if player.isPitcher && !player.isHitter { return nil }

    let isHome: Bool
    if feed.homeBattingOrder.contains(player.id) {
        isHome = true
    } else if feed.awayBattingOrder.contains(player.id) {
        isHome = false
    } else {
        return nil
    }

    let battingOrder = isHome ? feed.homeBattingOrder : feed.awayBattingOrder
    guard let playerIndex = battingOrder.firstIndex(of: player.id) else { return nil }

    // Between half-innings MLB keeps currentBatter/inningHalf as stale holdover from the
    // previous play, so the 3rd-out hitter would still look "at bat" until play resumes.
    let isBreak = feed.inningState == "Middle" || feed.inningState == "End"
    let teamIsBatting = !isBreak && ((isHome && feed.inningHalf == "Bottom") || (!isHome && feed.inningHalf == "Top"))

    guard teamIsBatting, let currentBatterID = feed.currentBatterID,
          let currentIndex = battingOrder.firstIndex(of: currentBatterID) else {
        return .notBatting(spot: playerIndex)
    }

    let count = battingOrder.count
    let distance = (playerIndex - currentIndex + count) % count
    switch distance {
    case 0: return .atBat
    case 1: return .onDeck
    case 2: return .dueUp
    default: return .order(distance)
    }
}

struct MenuBarView: View {
    let appState: AppState
    var isFloating = false
    @Namespace private var playerAnimation

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ActiveSection(appState: appState, isFloating: isFloating, namespace: playerAnimation)
            InGameSection(appState: appState, isFloating: isFloating, namespace: playerAnimation)
            UpcomingSection(appState: appState, isFloating: isFloating, namespace: playerAnimation)
            DoneSection(appState: appState, isFloating: isFloating, namespace: playerAnimation)
            EmptySection(appState: appState, isFloating: isFloating)
            ErrorSection(appState: appState)
            if !isFloating {
                FooterButtons(appState: appState)
            }
        }
        .frame(width: 300)
        #if DEBUG
        .overlay(alignment: .top) {
            let mb = appState.memoryStats.currentMB
            let color: Color = mb >= 200 ? .red : mb >= 150 ? .yellow : .secondary
            Text("\(mb) / \(appState.memoryStats.maxMB) MB")
                .font(.caption2)
                .foregroundStyle(color)
                .padding(.top, 10)
        }
        #endif
    }
}

// MARK: - Section Views

private struct ActiveSection: View {
    let appState: AppState
    let isFloating: Bool
    var namespace: Namespace.ID

    var body: some View {
        if !appState.activePlayers.isEmpty {
            SectionHeader(title: "Active Now", showClose: true, isFloating: isFloating, appState: appState)
            ForEach(appState.activePlayers) { player in
                LivePlayerRow(
                    player: player,
                    proximity: battingProximity(for: player, in: appState),
                    appState: appState,
                    isFloating: isFloating
                )
                .matchedGeometryEffect(id: player.id, in: namespace)
            }
            SectionDivider()
        }
    }
}

private struct InGameSection: View {
    let appState: AppState
    let isFloating: Bool
    var namespace: Namespace.ID

    var body: some View {
        if !appState.inGamePlayers.isEmpty {
            // Compute proximity once per player; reuse for sort, animation, and row rendering.
            let entries = appState.inGamePlayers
                .map { player -> (player: Player, proximity: BattingProximity?, sortKey: Int) in
                    let proximity = battingProximity(for: player, in: appState)
                    return (player, proximity, inGameSortKey(for: player, proximity: proximity, in: appState))
                }
                .sorted { $0.sortKey < $1.sortKey }
            SectionHeader(
                title: "In Game",
                showClose: appState.activePlayers.isEmpty,
                isFloating: isFloating,
                appState: appState
            )
            ForEach(entries, id: \.player.id) { entry in
                LivePlayerRow(
                    player: entry.player,
                    proximity: entry.proximity,
                    appState: appState,
                    isFloating: isFloating
                )
                .matchedGeometryEffect(id: entry.player.id, in: namespace)
            }
            .animation(.easeInOut(duration: 0.3), value: entries.map(\.sortKey))
            SectionDivider()
        }
    }
}

private struct UpcomingSection: View {
    let appState: AppState
    let isFloating: Bool
    var namespace: Namespace.ID

    var body: some View {
        if !appState.upcomingPlayers.isEmpty {
            SectionHeader(
                title: "Upcoming",
                showClose: appState.activePlayers.isEmpty && appState.inGamePlayers.isEmpty,
                isFloating: isFloating,
                appState: appState
            )
            ForEach(appState.upcomingPlayers) { player in
                UpcomingPlayerRow(player: player, appState: appState)
                    .matchedGeometryEffect(id: player.id, in: namespace)
            }
            if !appState.donePlayers.isEmpty {
                SectionDivider()
            } else if isFloating {
                Spacer().frame(height: 8)
            } else {
                SectionDivider()
            }
        }
    }
}

private struct DoneSection: View {
    let appState: AppState
    let isFloating: Bool
    var namespace: Namespace.ID

    var body: some View {
        if !appState.donePlayers.isEmpty {
            let showClose = appState.activePlayers.isEmpty && appState.inGamePlayers.isEmpty && appState.upcomingPlayers.isEmpty
            SectionHeader(title: "Done", showClose: showClose, isFloating: isFloating, appState: appState)
            ForEach(appState.donePlayers) { player in
                DonePlayerRow(player: player, appState: appState)
                    .matchedGeometryEffect(id: player.id, in: namespace)
            }
            if isFloating {
                Spacer().frame(height: 8)
            } else {
                SectionDivider()
            }
        }
    }
}

private struct EmptySection: View {
    let appState: AppState
    let isFloating: Bool

    var body: some View {
        if appState.activePlayers.isEmpty && appState.upcomingPlayers.isEmpty
            && appState.inGamePlayers.isEmpty && appState.donePlayers.isEmpty {
            if appState.rosterManager.isSyncing {
                emptyText("Syncing roster...")
            } else if appState.rosterURL.isEmpty {
                emptyText("Set roster URL in Settings")
            } else if appState.rosterManager.players.isEmpty {
                emptyText("No players found")
            } else {
                emptyText("No games today")
            }
            if isFloating {
                Spacer().frame(height: 8)
            } else {
                SectionDivider()
            }
        }
    }

    private func emptyText(_ text: String) -> some View {
        Text(text)
            .foregroundStyle(.secondary)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
    }
}

private struct ErrorSection: View {
    let appState: AppState

    var body: some View {
        if let error = appState.rosterManager.error ?? appState.scheduleManager.error {
            HStack(spacing: 4) {
                Image(systemName: "exclamationmark.triangle")
                Text(error)
            }
            .foregroundStyle(.red)
            .font(.caption)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            SectionDivider()
        }
    }
}

// MARK: - Shared Components

private struct SectionHeader: View {
    let title: String
    let showClose: Bool
    let isFloating: Bool
    let appState: AppState

    var body: some View {
        HStack {
            Text(title)
                .font(.caption)
                .fontWeight(.semibold)
                .foregroundStyle(.secondary)
                .textCase(.uppercase)
            Spacer()
            if showClose && isFloating {
                FloatingRefreshButton(appState: appState)
                Button {
                    FloatingPanel.shared.close()
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 10)
        .padding(.bottom, 4)
    }
}

private struct SectionDivider: View {
    var body: some View {
        Rectangle()
            .fill(.quaternary)
            .frame(height: 1)
            .padding(.vertical, 4)
    }
}

// MARK: - Row Views

private struct LivePlayerRow: View {
    let player: Player
    let proximity: BattingProximity?
    let appState: AppState
    let isFloating: Bool

    private var isActive: Bool {
        if case .active = appState.stateManager.playerStates[player.id] { return true }
        return false
    }

    private var game: Game? {
        appState.games.first { game in
            game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
                || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
        }
    }

    private var feed: LiveFeedData? {
        guard let game else { return nil }
        return appState.gameMonitor.latestFeeds[game.id]
    }

    private var isInLineup: Bool {
        guard let game else { return false }
        guard let side = game.side(for: player),
              let lineup = appState.gameMonitor.lineupPlayerIDs[game.id],
              lineup.isSubmitted(for: side) else {
            return true // Assume in lineup until that side's card is filed
        }
        return lineup.ids(for: side).contains(player.id)
    }

    private var showsProximityDot: Bool {
        switch proximity {
        case .atBat, .onDeck, .dueUp: true
        default: false
        }
    }

    var body: some View {
        Button { openStream() } label: {
            Group {
                if let feed {
                    HStack(alignment: .center, spacing: 8) {
                        VStack(alignment: .leading, spacing: 1) {
                            HStack(spacing: 4) {
                                switch proximity {
                                case .atBat:
                                    Circle()
                                        .fill(.green)
                                        .frame(width: 6, height: 6)
                                case .onDeck:
                                    Circle()
                                        .strokeBorder(.green, lineWidth: 1.5)
                                        .frame(width: 6, height: 6)
                                case .dueUp:
                                    Circle()
                                        .strokeBorder(.orange, lineWidth: 1.5)
                                        .frame(width: 6, height: 6)
                                case .order, .notBatting:
                                    EmptyView()
                                case nil:
                                    if isActive {
                                        Circle()
                                            .fill(.green)
                                            .frame(width: 6, height: 6)
                                    }
                                }
                                Text(player.name)
                                    .fontWeight(isActive ? .semibold : .medium)
                                    .lineLimit(1)
                            }
                            if let game, let text = formattedStatLine(gamePk: game.id) {
                                Text(text)
                                    .font(.system(size: 12))
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                    .truncationMode(.tail)
                                    .padding(.leading, isActive ? 10 : 0)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        HStack(alignment: .center, spacing: 16) {
                            ScoreBlock(
                                awayTeamID: feed.awayTeamID, awayScore: feed.awayScore,
                                homeTeamID: feed.homeTeamID, homeScore: feed.homeScore
                            )
                            VStack(spacing: -3) {
                                BasesDiagram(
                                    first: feed.runnerOnFirst,
                                    second: feed.runnerOnSecond,
                                    third: feed.runnerOnThird,
                                    highlightID: player.id
                                )
                                HStack(spacing: 1) {
                                    Image(systemName: feed.inningHalf == "Top" ? "arrowtriangle.up.fill" : "arrowtriangle.down.fill")
                                        .font(.system(size: 8.5))
                                    Text("\(feed.inning ?? 0)")
                                        .font(.system(size: 13, weight: .semibold))
                                }
                                .foregroundStyle(.secondary)
                                .offset(x: -2)
                            }
                            .offset(y: 2)
                            VStack(spacing: 4) {
                                Text(feed.isPlayComplete || (feed.balls == 0 && feed.strikes == 0) ? " " : "\(feed.balls)-\(feed.strikes)")
                                    .font(.system(size: 12, weight: .semibold))
                                    .monospacedDigit()
                                OutsIndicator(outs: feed.outs)
                            }
                            .offset(y: 3)
                        }
                        .fixedSize()
                    }
                } else {
                    HStack {
                        Text(player.name)
                            .fontWeight(.medium)
                        Spacer()
                        Text("In Game")
                            .font(.caption)
                            .foregroundStyle(.green)
                    }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 4)
        }
        .buttonStyle(MenuRowButtonStyle())
    }

    private func formattedStatLine(gamePk: Int) -> String? {
        if !isInLineup { return "Not in Lineup" }
        guard let feed = appState.gameMonitor.latestFeeds[gamePk] else { return nil }
        let statLine: String? = player.isPitcher && !player.isHitter
            ? feed.playerStats[player.id]?.pitching?.formatted
            : feed.playerStats[player.id]?.batting?.formatted
        if let delay = delayLabel(detailedState: feed.detailedState) {
            if let statLine { return "\(delay) · \(statLine)" }
            return delay
        }
        if player.isPitcher && !player.isHitter {
            return statLine
        }
        let prefix: String? = switch proximity {
        case .atBat, nil: nil
        case .onDeck: "On Deck"
        case .dueUp: "In Hole"
        case .order, .notBatting: nil
        }
        switch (prefix, statLine) {
        case let (p?, b?): return "\(p) · \(b)"
        case let (p?, nil): return p
        case let (nil, b?): return b
        case (nil, nil): return nil
        }
    }

    /// Mid-game pauses ("Delayed: Rain", "Suspended: Rain") - pre-game delays have
    /// abstractGameState "Preview" and don't reach this path.
    private func delayLabel(detailedState: String?) -> String? {
        guard let detailed = detailedState else { return nil }
        if detailed.hasPrefix("Delayed: ") {
            return "\(detailed.dropFirst("Delayed: ".count)) Delay"
        }
        if detailed.hasPrefix("Suspended: ") {
            return "Suspended: \(detailed.dropFirst("Suspended: ".count))"
        }
        if detailed == "Delayed" { return "Delayed" }
        if detailed == "Suspended" { return "Suspended" }
        return nil
    }

    private func openStream() {
        guard let game else { return }
        if !isFloating { dismissMenuBarWindow() }
        let url = StreamLinkRouter.url(for: game)
        NSWorkspace.shared.open(url)
    }
}

private func dismissMenuBarWindow() {
    NSApp.keyWindow?.close()
    DispatchQueue.main.async {
        if NSApp.keyWindow == nil {
            NSApp.deactivate()
        }
    }
}

private struct UpcomingPlayerRow: View {
    let player: Player
    let appState: AppState

    private var game: Game? {
        appState.games.first { game in
            game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
                || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
        }
    }

    private var lineupInfo: LineupInfo {
        guard let game,
              let side = game.side(for: player),
              let lineup = appState.gameMonitor.lineupPlayerIDs[game.id],
              lineup.isSubmitted(for: side) else { return .unknown }
        guard lineup.ids(for: side).contains(player.id) else { return .notInLineup }
        // Check live feed first, then fall back to schedule lineup data
        if let feed = appState.gameMonitor.latestFeeds[game.id] {
            if let idx = feed.homeBattingOrder.firstIndex(of: player.id) {
                return .battingOrder(idx + 1)
            }
            if let idx = feed.awayBattingOrder.firstIndex(of: player.id) {
                return .battingOrder(idx + 1)
            }
        }
        if let idx = game.homeLineup.firstIndex(of: player.id) {
            return .battingOrder(idx + 1)
        }
        if let idx = game.awayLineup.firstIndex(of: player.id) {
            return .battingOrder(idx + 1)
        }
        return .inLineup
    }

    private enum LineupInfo {
        case unknown, notInLineup, inLineup, battingOrder(Int)
    }

    var body: some View {
        HStack(spacing: 4) {
            Group {
                switch lineupInfo {
                case .notInLineup:
                    Circle()
                        .fill(.red)
                        .frame(width: 6, height: 6)
                case .battingOrder(let n):
                    Text("\(n)")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(.secondary)
                case .inLineup:
                    Image(systemName: "checkmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.green)
                case .unknown:
                    EmptyView()
                }
            }
            .frame(width: 14, alignment: .center)
            Text(player.name)
            Spacer()
            if case .upcoming(let startTime) = appState.stateManager.playerStates[player.id] {
                Text(startTime, style: .time)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
    }
}

private struct DonePlayerRow: View {
    let player: Player
    let appState: AppState

    var body: some View {
        HStack {
            Text(player.name)
                .foregroundStyle(.secondary)
            Spacer()
            if let gamePk = doneGamePk,
               let line = statLine(gamePk: gamePk) {
                Text(line)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
    }

    private var doneGamePk: Int? {
        guard case .inactive(let reason) = appState.stateManager.playerStates[player.id] else { return nil }
        switch reason {
        case .gameOver(let gamePk): return gamePk
        case .substituted(let gamePk): return gamePk
        case .dayOff: return nil
        }
    }

    private func statLine(gamePk: Int) -> String? {
        guard let feed = appState.gameMonitor.latestFeeds[gamePk],
              let stats = feed.playerStats[player.id] else { return nil }
        if player.isPitcher && !player.isHitter {
            return stats.pitching?.formatted
        }
        return stats.batting?.formatted
    }
}

private struct ScoreBlock: View {
    let awayTeamID: Int
    let awayScore: Int
    let homeTeamID: Int
    let homeScore: Int

    var body: some View {
        VStack(alignment: .trailing, spacing: 2) {
            HStack(spacing: 6) {
                TeamLogo(teamID: awayTeamID, size: 16)
                Text("\(awayScore)")
                    .monospacedDigit()
            }
            HStack(spacing: 6) {
                TeamLogo(teamID: homeTeamID, size: 16)
                Text("\(homeScore)")
                    .monospacedDigit()
            }
        }
        .font(.system(size: 16, weight: .semibold))
        .fixedSize()
    }
}

// MARK: - Menu Row Button Style

struct MenuRowButtonStyle: ButtonStyle {
    @State private var isHovered = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(
                RoundedRectangle(cornerRadius: 4)
                    .fill(isHovered ? .white.opacity(0.1) : .clear)
            )
            .onHover { isHovered = $0 }
    }
}

// MARK: - Footer Button Style

struct FooterButtonStyle: ButtonStyle {
    @State private var isHovered = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill(isHovered ? .white.opacity(0.1) : .clear)
            )
            .onHover { isHovered = $0 }
    }
}

// MARK: - Bases Diamond

struct BasesDiagram: View {
    let first: Int?
    let second: Int?
    let third: Int?
    var highlightID: Int? = nil

    var body: some View {
        ZStack {
            diamond(runnerID: second)
                .offset(y: -7)
            diamond(runnerID: third)
                .offset(x: -10.5, y: 3.5)
            diamond(runnerID: first)
                .offset(x: 10.5, y: 3.5)
        }
        .frame(width: 35, height: 24)
    }

    private func diamond(runnerID: Int?) -> some View {
        let occupied = runnerID != nil
        let highlighted = occupied && runnerID == highlightID
        return Image(systemName: occupied ? "diamond.fill" : "diamond")
            .font(.system(size: 14))
            .foregroundStyle(highlighted ? .green : occupied ? .white : .gray.opacity(0.3))
    }
}

// MARK: - Outs Indicator

struct OutsIndicator: View {
    let outs: Int

    var body: some View {
        HStack(spacing: 4) {
            ForEach(0..<3, id: \.self) { i in
                Circle()
                    .fill(i < outs ? .white : .gray.opacity(0.3))
                    .frame(width: 7, height: 7)
            }
        }
    }
}

// MARK: - Team Logo

struct TeamLogo: View {
    let teamID: Int
    let size: CGFloat

    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fill)
                    .scaleEffect(1.7)
            } else {
                Color.clear
            }
        }
        .frame(width: size, height: size)
        .clipped()
        .task(id: teamID) {
            image = await TeamLogoCache.shared.logo(for: teamID, size: Int(size * 2))
        }
    }
}

@MainActor
final class TeamLogoCache {
    static let shared = TeamLogoCache()

    private var memory: [String: NSImage] = [:]
    private let cacheDir: URL = {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("TeamLogos", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    func logo(for teamID: Int, size: Int) async -> NSImage? {
        let key = "\(teamID)_\(size)"

        if let cached = memory[key] { return cached }

        let file = cacheDir.appendingPathComponent("\(key).png")
        if let diskImage = NSImage(contentsOf: file) {
            memory[key] = diskImage
            return diskImage
        }

        guard let url = URL(string: "https://midfield.mlbstatic.com/v1/team/\(teamID)/spots/\(size)") else { return nil }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let image = NSImage(data: data) else { return nil }
            memory[key] = image
            try? data.write(to: file)
            return image
        } catch {
            return nil
        }
    }

    /// Drops the in-memory NSImage references. The PNGs remain on disk, so the
    /// next logo access reloads in ~1 ms. Called from `MemoryPressureRelief` at
    /// idle transitions.
    func evictMemoryCache() {
        memory.removeAll(keepingCapacity: false)
    }
}

// MARK: - Footer Buttons

struct FooterButtons: View {
    let appState: AppState
    @Environment(\.openSettings) private var openSettings
    @State private var refreshState: RefreshButtonState = .idle

    private enum RefreshButtonState {
        case idle, spinning, done, failed
    }

    var body: some View {
        HStack(spacing: 0) {
            footerButton(systemIcon: "gear", label: "Settings") {
                dismissMenu()
                // The activation-policy flip is load-bearing: it lets macOS unload the
                // Settings window infrastructure when SettingsView.onDisappear flips
                // back to .accessory. Without it the ~230 MB spike from openSettings()
                // sits resident until the OS decides to release on its own schedule.
                NSApp.setActivationPolicy(.regular)
                NSApp.activate()
                openSettings()
            }
            if let leagueID = appState.parsedLeagueID {
                footerButton(assetIcon: "FantraxIcon", label: "Fantrax") {
                    dismissMenu()
                    if let url = URL(string: "https://www.fantrax.com/fantasy/league/\(leagueID)/home") {
                        NSWorkspace.shared.open(url)
                    }
                }
            }
            refreshButton
            footerButton(systemIcon: FloatingPanel.shared.isShowing ? "pip.exit" : "pip.enter", label: "Float") {
                dismissMenu()
                FloatingPanel.shared.toggle(appState: appState)
            }
            Spacer()
            footerButton(systemIcon: "power", label: "Quit") {
                NSApplication.shared.terminate(nil)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 2)
        .padding(.bottom, 6)
    }

    private func footerButton(systemIcon: String, label: String, action: @escaping () -> Void) -> some View {
        footerButtonView(icon: Image(systemName: systemIcon).font(.system(size: 16)), label: label, action: action)
    }

    private func footerButton(assetIcon: String, label: String, action: @escaping () -> Void) -> some View {
        footerButtonView(icon: Image(assetIcon).resizable().aspectRatio(contentMode: .fit).frame(width: 16, height: 16), label: label, action: action)
    }

    private func footerButtonView<I: View>(icon: I, label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 3) {
                icon.frame(width: 24, height: 20, alignment: .center)
                Text(label)
                    .font(.system(size: 10))
                    .fixedSize()
            }
            .frame(width: 52, height: 42)
            .contentShape(Rectangle())
        }
        .buttonStyle(FooterButtonStyle())
        .foregroundStyle(.secondary)
    }

    private var refreshButton: some View {
        Button {
            guard refreshState == .idle else { return }
            refreshState = .spinning
            Task {
                let success = await appState.resyncRoster()
                refreshState = success ? .done : .failed
                try? await Task.sleep(for: .seconds(1.2))
                refreshState = .idle
            }
        } label: {
            VStack(spacing: 3) {
                ZStack {
                    if refreshState == .spinning {
                        ProgressView()
                            .scaleEffect(0.5)
                            .transition(.opacity)
                    } else if refreshState == .done {
                        Image(systemName: "checkmark")
                            .font(.system(size: 16, weight: .semibold))
                            .transition(.opacity)
                    } else if refreshState == .failed {
                        Image(systemName: "xmark")
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(.red)
                            .transition(.opacity)
                    } else {
                        Image(systemName: "arrow.clockwise")
                            .font(.system(size: 16))
                            .transition(.opacity)
                    }
                }
                .animation(.easeInOut(duration: 0.3), value: refreshState)
                .frame(width: 24, height: 20, alignment: .center)
                Text("Refresh")
                    .font(.system(size: 10))
                    .fixedSize()
            }
            .frame(width: 52, height: 42)
            .contentShape(Rectangle())
        }
        .buttonStyle(FooterButtonStyle())
        .foregroundStyle(.secondary)
    }

    private func dismissMenu() {
        dismissMenuBarWindow()
    }
}

// MARK: - Floating Refresh Button

private struct FloatingRefreshButton: View {
    let appState: AppState
    @State private var state: RefreshState = .idle

    private enum RefreshState {
        case idle, spinning, done, failed
    }

    var body: some View {
        Button {
            guard state == .idle else { return }
            state = .spinning
            Task {
                let success = await appState.resyncRoster()
                state = success ? .done : .failed
                try? await Task.sleep(for: .seconds(1.2))
                state = .idle
            }
        } label: {
            ZStack {
                if state == .spinning {
                    ProgressView()
                        .scaleEffect(0.35)
                        .transition(.opacity)
                } else if state == .done {
                    Image(systemName: "checkmark.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)
                        .transition(.opacity)
                } else if state == .failed {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(.red)
                        .transition(.opacity)
                } else {
                    Image(systemName: "arrow.clockwise.circle.fill")
                        .font(.system(size: 14))
                        .foregroundStyle(.secondary)
                        .transition(.opacity)
                }
            }
            .frame(width: 14, height: 14)
            .animation(.easeInOut(duration: 0.3), value: state)
        }
        .buttonStyle(.plain)
    }
}

// MARK: - Floating Panel

@MainActor
final class FloatingPanel {
    static let shared = FloatingPanel()
    fileprivate var panel: NSPanel?

    var isShowing: Bool { panel != nil }

    func toggle(appState: AppState) {
        if let panel {
            panel.close()
            self.panel = nil
        } else {
            show(appState: appState)
        }
    }

    private func show(appState: AppState) {
        let content = MenuBarView(appState: appState, isFloating: true)
            .background {
                Color.black.opacity(0.25)
                    .background(.ultraThinMaterial.opacity(0.85))
            }
            .clipShape(RoundedRectangle(cornerRadius: 12))

        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 300, height: 500),
            styleMask: [.borderless, .nonactivatingPanel, .utilityWindow],
            backing: .buffered,
            defer: false
        )
        let hostingView = NSHostingView(rootView: content)
        hostingView.translatesAutoresizingMaskIntoConstraints = false
        panel.contentView = NSView()
        panel.contentView!.addSubview(hostingView)
        NSLayoutConstraint.activate([
            hostingView.leadingAnchor.constraint(equalTo: panel.contentView!.leadingAnchor),
            hostingView.trailingAnchor.constraint(equalTo: panel.contentView!.trailingAnchor),
            hostingView.topAnchor.constraint(equalTo: panel.contentView!.topAnchor),
            hostingView.bottomAnchor.constraint(equalTo: panel.contentView!.bottomAnchor),
        ])
        panel.isFloatingPanel = true
        panel.level = .floating
        panel.isMovableByWindowBackground = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.isReleasedWhenClosed = false
        panel.setFrameAutosaveName("FloatingPanel")
        if !panel.setFrameUsingName("FloatingPanel") {
            panel.center()
        }
        panel.makeKeyAndOrderFront(nil)
        panel.delegate = PanelCloseDelegate.shared

        self.panel = panel
    }

    func close() {
        panel?.close()
        panel = nil
    }
}

private class PanelCloseDelegate: NSObject, NSWindowDelegate {
    static let shared = PanelCloseDelegate()

    func windowWillClose(_ notification: Notification) {
        FloatingPanel.shared.panel = nil
    }
}
