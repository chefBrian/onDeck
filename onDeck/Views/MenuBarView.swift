import SwiftUI

struct MenuBarView: View {
    let appState: AppState

    var body: some View {
        VStack(alignment: .leading) {
            if !appState.activePlayers.isEmpty {
                Section("Active Now") {
                    ForEach(appState.activePlayers) { player in
                        Text(player.name)
                    }
                }
                Divider()
            }

            if !appState.upcomingPlayers.isEmpty {
                Section("Upcoming") {
                    ForEach(appState.upcomingPlayers) { player in
                        Text(player.name)
                    }
                }
                Divider()
            }

            if !appState.inactivePlayers.isEmpty {
                Section("Done / Off") {
                    ForEach(appState.inactivePlayers) { player in
                        Text(player.name)
                    }
                }
                Divider()
            }

            if appState.activePlayers.isEmpty && appState.upcomingPlayers.isEmpty {
                Text("No games today")
                    .foregroundStyle(.secondary)
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
}
