import SwiftUI

struct MenuBarView: View {
    @Bindable var appState: AppState
    var isFloating = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            activeSection
            inGameSection
            upcomingSection
            doneSection
            emptySection
            errorSection
            footerSection
        }
        .frame(width: 300)
        .transaction { $0.animation = nil }
    }

    // MARK: - Sections

    @ViewBuilder
    private var activeSection: some View {
        if !appState.activePlayers.isEmpty {
            sectionHeader("Active Now", showClose: true)
            ForEach(appState.activePlayers) { player in
                activePlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var inGameSection: some View {
        if !appState.inGamePlayers.isEmpty {
            sectionHeader("In Game", showClose: !appState.activePlayers.isEmpty ? false : true)
            ForEach(appState.inGamePlayers) { player in
                inGamePlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var upcomingSection: some View {
        if !appState.upcomingPlayers.isEmpty {
            sectionHeader("Upcoming", showClose: appState.activePlayers.isEmpty && appState.inGamePlayers.isEmpty)
            ForEach(appState.upcomingPlayers) { player in
                upcomingPlayerRow(player)
            }
            if !appState.donePlayers.isEmpty {
                divider()
            } else if isFloating {
                Spacer().frame(height: 8)
            } else {
                divider()
            }
        }
    }

    @ViewBuilder
    private var doneSection: some View {
        let played = appState.donePlayers.filter { player in
            guard let gamePk = doneGamePk(for: player) else { return true }
            return statLine(for: player, gamePk: gamePk) != nil
        }
        if !played.isEmpty {
            let showClose = appState.activePlayers.isEmpty && appState.inGamePlayers.isEmpty && appState.upcomingPlayers.isEmpty
            sectionHeader("Done", showClose: showClose)
            ForEach(played) { player in
                donePlayerRow(player)
            }
            if isFloating {
                Spacer().frame(height: 8)
            } else {
                divider()
            }
        }
    }

    @ViewBuilder
    private var emptySection: some View {
        if appState.activePlayers.isEmpty && appState.upcomingPlayers.isEmpty
            && appState.inGamePlayers.isEmpty && appState.donePlayers.isEmpty {
            emptyState()
            if isFloating {
                Spacer().frame(height: 8)
            } else {
                divider()
            }
        }
    }

    @ViewBuilder
    private var errorSection: some View {
        if let error = appState.rosterManager.error ?? appState.scheduleManager.error {
            HStack(spacing: 4) {
                Image(systemName: "exclamationmark.triangle")
                Text(error)
            }
            .foregroundStyle(.red)
            .font(.caption)
            .padding(.horizontal, 12)
            .padding(.vertical, 6)
            divider()
        }
    }

    @ViewBuilder
    private var footerSection: some View {
        if !isFloating {
            FooterButtons(appState: appState)
        }
    }

    // MARK: - Reusable Components

    private func sectionHeader(_ title: String, showClose: Bool = false) -> some View {
        HStack {
            Text(title)
                .font(.caption)
                .fontWeight(.semibold)
                .foregroundStyle(.secondary)
                .textCase(.uppercase)
            Spacer()
            if showClose && isFloating {
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

    private func divider() -> some View {
        Rectangle()
            .fill(.quaternary)
            .frame(height: 1)
            .padding(.vertical, 4)
    }

    // MARK: - Row Views

    private func activePlayerRow(_ player: Player) -> some View {
        livePlayerRow(player)
    }

    private func statLine(for player: Player, gamePk: Int) -> String? {
        guard let feed = appState.gameMonitor.latestFeeds[gamePk],
              let stats = feed.playerStats[player.id] else { return nil }
        if player.isPitcher && !player.isHitter {
            return stats.pitchingLine
        }
        return stats.battingLine
    }

    private func scoreBlock(awayTeamID: Int, awayScore: Int, homeTeamID: Int, homeScore: Int) -> some View {
        VStack(alignment: .trailing, spacing: 2) {
            HStack(spacing: 6) {
                Text("\(awayScore)")
                    .monospacedDigit()
                TeamLogo(teamID: awayTeamID, size: 16)
            }
            HStack(spacing: 6) {
                Text("\(homeScore)")
                    .monospacedDigit()
                TeamLogo(teamID: homeTeamID, size: 16)
            }
        }
        .font(.system(size: 16, weight: .semibold))
        .frame(width: 50, alignment: .trailing)
    }

    private func inningLabel(_ inning: String) -> some View {
        HStack(spacing: 2) {
            Image(systemName: inning.hasPrefix("Top") ? "arrowtriangle.up.fill" : "arrowtriangle.down.fill")
                .font(.system(size: 12))
            Text(inning.filter(\.isNumber))
                .font(.system(size: 15, weight: .semibold))
        }
        .foregroundStyle(.secondary)
    }

    private func inGamePlayerRow(_ player: Player) -> some View {
        livePlayerRow(player)
    }

    private func livePlayerRow(_ player: Player) -> some View {
        let isActive: Bool = {
            if case .active = appState.stateManager.playerStates[player.id] { return true }
            return false
        }()

        return Button { openStream(for: player) } label: {
            Group {
                if let feed = feedForPlayer(player) {
                    HStack(alignment: .center, spacing: 10) {
                        VStack(alignment: .leading, spacing: 1) {
                            HStack(spacing: 4) {
                                if isActive {
                                    Circle()
                                        .fill(.green)
                                        .frame(width: 6, height: 6)
                                }
                                Text(player.name)
                                    .fontWeight(isActive ? .semibold : .medium)
                                    .lineLimit(1)
                            }
                            if let game = gameForPlayer(player),
                               let line = statLine(for: player, gamePk: game.id) {
                                Text(line)
                                    .font(.system(size: 12))
                                    .foregroundStyle(.secondary)
                                    .padding(.leading, isActive ? 10 : 0)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        HStack(alignment: .center, spacing: 5) {
                            VStack(spacing: -3) {
                                BasesDiagram(
                                    first: feed.runnerOnFirst,
                                    second: feed.runnerOnSecond,
                                    third: feed.runnerOnThird
                                )
                                HStack(spacing: 1) {
                                    Image(systemName: feed.inningHalf == "Top" ? "arrowtriangle.up.fill" : "arrowtriangle.down.fill")
                                        .font(.system(size: 7))
                                    Text("\(feed.inning ?? 0)")
                                        .font(.system(size: 11, weight: .semibold))
                                }
                                .foregroundStyle(.secondary)
                            }
                            VStack(spacing: 2) {
                                Text("\(feed.balls)-\(feed.strikes)")
                                    .font(.system(size: 11, weight: .semibold))
                                    .monospacedDigit()
                                OutsIndicator(outs: feed.outs)
                            }
                        }
                        .fixedSize()
                        scoreBlock(
                            awayTeamID: feed.awayTeamID, awayScore: feed.awayScore,
                            homeTeamID: feed.homeTeamID, homeScore: feed.homeScore
                        )
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

    private func upcomingPlayerRow(_ player: Player) -> some View {
        HStack {
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

    private func donePlayerRow(_ player: Player) -> some View {
        HStack {
            Text(player.name)
                .foregroundStyle(.secondary)
            Spacer()
            if let gamePk = doneGamePk(for: player),
               let line = statLine(for: player, gamePk: gamePk) {
                Text(line)
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
    }

    private func doneGamePk(for player: Player) -> Int? {
        guard case .inactive(let reason) = appState.stateManager.playerStates[player.id] else { return nil }
        switch reason {
        case .gameOver(let gamePk): return gamePk
        case .substituted(let gamePk): return gamePk
        case .dayOff: return nil
        }
    }

    @ViewBuilder
    private func emptyState() -> some View {
        if appState.rosterManager.isSyncing {
            Text("Syncing roster...")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
        } else if appState.rosterURL.isEmpty {
            Text("Set roster URL in Settings")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
        } else if appState.rosterManager.players.isEmpty {
            Text("No players found")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
        } else {
            Text("No games today")
                .foregroundStyle(.secondary)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
        }
    }

    // MARK: - Helpers



    private func feedForPlayer(_ player: Player) -> LiveFeedData? {
        guard let game = gameForPlayer(player) else { return nil }
        return appState.gameMonitor.latestFeeds[game.id]
    }

    private func gameForPlayer(_ player: Player) -> Game? {
        appState.games.first { game in
            game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
                || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
        }
    }

    private func inningLabel(_ feed: LiveFeedData) -> some View {
        let str = [feed.inningHalf == "Top" ? "Top" : "Bot", feed.inning.map { "\($0)" } ?? ""]
            .joined(separator: " ")
        return inningLabel(str)
    }

    private func openStream(for player: Player) {
        guard let game = gameForPlayer(player) else { return }
        let url = StreamLinkRouter.url(for: game)
        NSWorkspace.shared.open(url)
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
    let first: Bool
    let second: Bool
    let third: Bool

    var body: some View {
        VStack(spacing: -6) {
            diamond(filled: second)
            HStack(spacing: 0) {
                diamond(filled: third)
                diamond(filled: first)
            }
        }
    }

    private func diamond(filled: Bool) -> some View {
        Image(systemName: filled ? "diamond.fill" : "diamond")
            .font(.system(size: 10))
            .foregroundStyle(filled ? .white : .gray.opacity(0.3))
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
                NSApplication.shared.activate()
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
        NSApp.keyWindow?.close()
        DispatchQueue.main.async {
            if NSApp.keyWindow == nil {
                NSApp.deactivate()
            }
        }
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
            .background(.ultraThinMaterial)
            .clipShape(RoundedRectangle(cornerRadius: 12))

        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 300, height: 500),
            styleMask: [.borderless, .nonactivatingPanel, .utilityWindow],
            backing: .buffered,
            defer: false
        )
        panel.contentView = NSHostingView(rootView: content)
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
        // Just nil out the reference, don't call close() again
        FloatingPanel.shared.panel = nil
    }
}
