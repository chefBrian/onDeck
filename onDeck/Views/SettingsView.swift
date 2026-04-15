import SwiftUI

struct SettingsView: View {
    @Bindable var appState: AppState
    @State private var notifyBatting = UserDefaults.standard.bool(forKey: "notifyBatting", default: true)
    @State private var notifyPitching = UserDefaults.standard.bool(forKey: "notifyPitching", default: true)
    @State private var notifyAtBatResult = UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true)
    @State private var notifyPitchingResult = UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true)
    @State private var notifyNotInLineup = UserDefaults.standard.bool(forKey: "notifyNotInLineup", default: true)

    var body: some View {
        Form {
            Section("Fantrax Roster") {
                TextField("League URL", text: $appState.rosterURL)
                    .onSubmit {
                        Task { await appState.fetchTeams() }
                    }

                // Team picker - shown when URL has no teamId
                if !appState.rosterURL.isEmpty && !appState.urlHasTeamID {
                    if appState.isLoadingTeams {
                        HStack {
                            ProgressView()
                                .controlSize(.small)
                            Text("Loading teams...")
                                .foregroundStyle(.secondary)
                        }
                    } else if !appState.availableTeams.isEmpty {
                        Picker("Team", selection: $appState.selectedTeamID) {
                            Text("Select a team...").tag("")
                            ForEach(appState.availableTeams) { team in
                                Text(team.name).tag(team.id)
                            }
                        }
                    } else if appState.availableTeams.isEmpty && appState.parsedLeagueID != nil {
                        Button("Load Teams") {
                            Task { await appState.fetchTeams() }
                        }
                    }

                    if let error = appState.teamsError {
                        Text(error)
                            .foregroundStyle(.red)
                            .font(.caption)
                    }
                }

                HStack {
                    if appState.rosterManager.isSyncing {
                        ProgressView()
                            .controlSize(.small)
                        Text("Syncing...")
                            .foregroundStyle(.secondary)
                    } else if let date = appState.rosterManager.lastSyncDate {
                        Text("Last synced: \(date, style: .relative) ago")
                            .foregroundStyle(.secondary)
                            .font(.caption)
                    }

                    Spacer()

                    Button("Sync Now") {
                        Task { await appState.resyncRoster() }
                    }
                    .disabled(appState.rosterManager.isSyncing || appState.effectiveTeamID == nil)
                }

                if let error = appState.rosterManager.error {
                    Text(error)
                        .foregroundStyle(.red)
                        .font(.caption)
                }

                if !appState.rosterManager.players.isEmpty {
                    Text("\(appState.rosterManager.players.count) players loaded")
                        .foregroundStyle(.secondary)
                        .font(.caption)
                }
            }

            Section("Display") {
                Toggle("Hide bench players", isOn: $appState.hideBenchPlayers)
                Toggle("Always open popout on launch", isOn: $appState.alwaysOpenPopout)
            }

            Section("Notifications") {
                Toggle("Stepping up to bat", isOn: $notifyBatting)
                    .onChange(of: notifyBatting) { _, new in
                        UserDefaults.standard.set(new, forKey: "notifyBatting")
                    }
                Toggle("Taking the mound", isOn: $notifyPitching)
                    .onChange(of: notifyPitching) { _, new in
                        UserDefaults.standard.set(new, forKey: "notifyPitching")
                    }
                Toggle("At-bat results", isOn: $notifyAtBatResult)
                    .onChange(of: notifyAtBatResult) { _, new in
                        UserDefaults.standard.set(new, forKey: "notifyAtBatResult")
                    }
                Toggle("Pitching results", isOn: $notifyPitchingResult)
                    .onChange(of: notifyPitchingResult) { _, new in
                        UserDefaults.standard.set(new, forKey: "notifyPitchingResult")
                    }
                Toggle("Not in lineup", isOn: $notifyNotInLineup)
                    .onChange(of: notifyNotInLineup) { _, new in
                        UserDefaults.standard.set(new, forKey: "notifyNotInLineup")
                    }
            }
            Section("Links") {
                Link("GitHub", destination: URL(string: "https://github.com/chefBrian/onDeck")!)
                Link("Report a Bug", destination: URL(string: "https://github.com/chefBrian/onDeck/issues")!)
            }
        }
        .formStyle(.grouped)
        .frame(width: 450, height: 400)
        .onAppear {
            NSApplication.shared.setActivationPolicy(.regular)
        }
        .onDisappear {
            NSApplication.shared.setActivationPolicy(.accessory)
        }
    }
}

extension UserDefaults {
    func bool(forKey key: String, default defaultValue: Bool) -> Bool {
        object(forKey: key) == nil ? defaultValue : bool(forKey: key)
    }
}
