import SwiftUI

struct MenuBarView: View {
    let appState: AppState

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
        .frame(width: 280)
        .transaction { $0.animation = nil }
    }

    // MARK: - Sections

    @ViewBuilder
    private var activeSection: some View {
        if !appState.activePlayers.isEmpty {
            sectionHeader("Active Now")
            ForEach(appState.activePlayers) { player in
                activePlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var inGameSection: some View {
        if !appState.inGamePlayers.isEmpty {
            sectionHeader("In Game")
            ForEach(appState.inGamePlayers) { player in
                inGamePlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var upcomingSection: some View {
        if !appState.upcomingPlayers.isEmpty {
            sectionHeader("Upcoming")
            ForEach(appState.upcomingPlayers) { player in
                upcomingPlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var doneSection: some View {
        if !appState.inactivePlayers.isEmpty {
            sectionHeader("Done / Off")
            ForEach(appState.inactivePlayers) { player in
                inactivePlayerRow(player)
            }
            divider()
        }
    }

    @ViewBuilder
    private var emptySection: some View {
        if appState.activePlayers.isEmpty && appState.upcomingPlayers.isEmpty
            && appState.inGamePlayers.isEmpty && appState.inactivePlayers.isEmpty {
            emptyState()
            divider()
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

    private var footerSection: some View {
        FooterButtons()
    }

    // MARK: - Reusable Components

    private func sectionHeader(_ title: String) -> some View {
        Text(title)
            .font(.caption)
            .fontWeight(.semibold)
            .foregroundStyle(.secondary)
            .textCase(.uppercase)
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
        Button { openStream(for: player) } label: {
            activePlayerContent(player)
        }
        .buttonStyle(MenuRowButtonStyle())
    }

    private func activePlayerContent(_ player: Player) -> some View {
        Group {
            if case .active(let ctx) = appState.stateManager.playerStates[player.id] {
                VStack(alignment: .leading, spacing: 6) {
                    playerHeader(player)
                    gameStateCard(ctx)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
            }
        }
    }

    private func playerHeader(_ player: Player) -> some View {
        HStack {
            Circle()
                .fill(.green)
                .frame(width: 6, height: 6)
            Text(player.name)
                .fontWeight(.semibold)
            Spacer()
            Text(player.isPitcher && !player.isHitter ? "Pitching" : "At Bat")
                .font(.caption2)
                .foregroundStyle(.green)
        }
    }

    private func gameStateCard(_ ctx: PlayerState.GameContext) -> some View {
        HStack(spacing: 12) {
            // Score
            VStack(alignment: .leading, spacing: 1) {
                scoreRow(team: ctx.awayTeam, score: ctx.awayScore,
                         isUp: ctx.inning.hasPrefix("Top"))
                scoreRow(team: ctx.homeTeam, score: ctx.homeScore,
                         isUp: ctx.inning.hasPrefix("Bot"))
            }
            .font(.system(size: 11, weight: .medium, design: .monospaced))

            Spacer()

            // Bases
            BasesDiagram(
                first: ctx.runnerOnFirst,
                second: ctx.runnerOnSecond,
                third: ctx.runnerOnThird
            )

            // Inning + Count + Outs
            VStack(alignment: .trailing, spacing: 3) {
                HStack(spacing: 1) {
                    Image(systemName: ctx.inning.hasPrefix("Top") ? "arrowtriangle.up.fill" : "arrowtriangle.down.fill")
                        .font(.system(size: 6))
                    Text(ctx.inning.filter(\.isNumber))
                }
                .font(.caption2)
                .foregroundStyle(.secondary)
                Text("\(ctx.balls)-\(ctx.strikes)")
                    .font(.system(size: 11, weight: .medium, design: .monospaced))
                OutsIndicator(outs: ctx.outs)
            }
        }
        .padding(8)
        .background(.white.opacity(0.05))
        .cornerRadius(6)
    }

    private func inGamePlayerRow(_ player: Player) -> some View {
        Button { openStream(for: player) } label: {
            inGamePlayerContent(player)
        }
        .buttonStyle(MenuRowButtonStyle())
    }

    private func inGamePlayerContent(_ player: Player) -> some View {
        HStack {
            Text(player.name)
            Spacer()
            if let feed = feedForPlayer(player) {
                let awayShort = feed.awayTeam.split(separator: " ").last.map(String.init) ?? feed.awayTeam
                let homeShort = feed.homeTeam.split(separator: " ").last.map(String.init) ?? feed.homeTeam
                Text("\(awayShort) \(feed.awayScore)-\(feed.homeScore) \(homeShort)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                HStack(spacing: 3) {
                    inningLabel(feed)
                    OutsIndicator(outs: feed.outs)
                }
                .font(.caption2)
                .foregroundStyle(.secondary)
            } else {
                Text("In Game")
                    .font(.caption)
                    .foregroundStyle(.green)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
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

    private func inactivePlayerRow(_ player: Player) -> some View {
        HStack {
            Text(player.name)
                .foregroundStyle(.secondary)
            Spacer()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 3)
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

    private func scoreRow(team: String, score: Int, isUp: Bool) -> some View {
        HStack(spacing: 4) {
            if isUp {
                Image(systemName: "arrowtriangle.right.fill")
                    .font(.system(size: 5))
                    .foregroundStyle(.green)
            } else {
                Color.clear.frame(width: 5, height: 5)
            }
            Text(team)
                .frame(width: 50, alignment: .leading)
                .lineLimit(1)
            Text("\(score)")
                .frame(width: 20, alignment: .trailing)
        }
    }

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
        HStack(spacing: 1) {
            if let half = feed.inningHalf {
                Image(systemName: half == "Top" ? "arrowtriangle.up.fill" : "arrowtriangle.down.fill")
                    .font(.system(size: 6))
            }
            if let inning = feed.inning {
                Text("\(inning)")
            }
        }
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

// MARK: - Bases Diamond

struct BasesDiagram: View {
    let first: Bool
    let second: Bool
    let third: Bool

    var body: some View {
        VStack(spacing: -1) {
            diamond(filled: second)
            HStack(spacing: 8) {
                diamond(filled: third)
                diamond(filled: first)
            }
        }
    }

    private func diamond(filled: Bool) -> some View {
        Image(systemName: filled ? "diamond.fill" : "diamond")
            .font(.system(size: 9))
            .foregroundStyle(filled ? .white : .gray.opacity(0.3))
    }
}

// MARK: - Outs Indicator

struct OutsIndicator: View {
    let outs: Int

    var body: some View {
        HStack(spacing: 3) {
            ForEach(0..<3, id: \.self) { i in
                Circle()
                    .fill(i < outs ? .white : .gray.opacity(0.3))
                    .frame(width: 6, height: 6)
            }
        }
    }
}

// MARK: - Footer Buttons

struct FooterButtons: View {
    @Environment(\.openSettings) private var openSettings

    var body: some View {
        HStack {
            Button {
                openSettings()
            } label: {
                Text("Settings...")
            }
            Spacer()
            Button {
                NSApplication.shared.terminate(nil)
            } label: {
                Text("Quit")
            }
        }
        .buttonStyle(MenuRowButtonStyle())
        .foregroundStyle(.secondary)
        .font(.caption)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
    }
}
