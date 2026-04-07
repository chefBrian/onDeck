import SwiftUI

struct MenuBarView: View {
    let appState: AppState

    var body: some View {
        VStack(alignment: .leading) {
            if !appState.activePlayers.isEmpty {
                Section("Active Now") {
                    ForEach(appState.activePlayers) { player in
                        activePlayerRow(player)
                    }
                }
                Divider()
            }

            if !appState.upcomingPlayers.isEmpty {
                Section("Upcoming") {
                    ForEach(appState.upcomingPlayers) { player in
                        upcomingPlayerRow(player)
                    }
                }
                Divider()
            }

            if !appState.inactivePlayers.isEmpty {
                Section("Done / Off") {
                    ForEach(appState.inactivePlayers) { player in
                        Text(player.name)
                            .foregroundStyle(.secondary)
                    }
                }
                Divider()
            }

            if appState.activePlayers.isEmpty && appState.upcomingPlayers.isEmpty
                && appState.inactivePlayers.isEmpty {
                if appState.rosterManager.isSyncing {
                    Text("Syncing roster...")
                        .foregroundStyle(.secondary)
                } else if appState.rosterURL.isEmpty {
                    Text("Set roster URL in Settings")
                        .foregroundStyle(.secondary)
                } else if appState.rosterManager.players.isEmpty {
                    Text("No players found")
                        .foregroundStyle(.secondary)
                } else {
                    Text("No games today")
                        .foregroundStyle(.secondary)
                }
                Divider()
            }

            if let error = appState.rosterManager.error ?? appState.scheduleManager.error {
                Label(error, systemImage: "exclamationmark.triangle")
                    .foregroundStyle(.red)
                    .font(.caption)
                Divider()
            }

            SettingsLink {
                Text("Settings...")
            }

            Divider()

            Button("Quit") {
                NSApplication.shared.terminate(nil)
            }
            .keyboardShortcut("q")
        }
    }

    // MARK: - Row Views

    private func activePlayerRow(_ player: Player) -> some View {
        Button {
            openStream(for: player)
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text(player.name)
                    .fontWeight(.medium)
                if case .active(let context) = appState.stateManager.playerStates[player.id] {
                    Text("\(context.inning) - \(context.score)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                    Text(context.count)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
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
    }

    // MARK: - Stream Links

    private func openStream(for player: Player) {
        guard let game = appState.games.first(where: { game in
            game.homeTeam.contains(player.team) || game.awayTeam.contains(player.team)
                || player.team.contains(game.homeTeam) || player.team.contains(game.awayTeam)
        }) else { return }

        let url = StreamLinkRouter.url(for: game)
        NSWorkspace.shared.open(url)
    }
}
