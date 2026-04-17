import SwiftUI

#if DEBUG
import os.log

/// Phase-1 toggle - set to `false` to measure Settings open/close with the
/// activation-policy flip disabled. Default `true` matches current behavior.
let SETTINGS_FLIP_ACTIVATION_POLICY = true

private let memoryLogger = Logger(subsystem: "dev.bjc.onDeck", category: "memory")

/// Counts legitimate Settings open events, ignoring spurious SwiftUI `.onAppear`
/// re-fires (e.g. when a child sheet dismisses). Increments only on transitions
/// from closed -> open; returns nil for re-fires so the caller can skip logging.
private actor SettingsCycleCounter {
    static let shared = SettingsCycleCounter()
    private var count = 0
    private var isOpen = false

    func recordOpen() -> Int? {
        if isOpen { return nil }
        isOpen = true
        count += 1
        return count
    }

    func recordClose() {
        isOpen = false
    }
}
#else
let SETTINGS_FLIP_ACTIVATION_POLICY = true
#endif

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
            Task { await handleOnAppear() }
        }
        .onDisappear {
            Task { await handleOnDisappear() }
        }
    }

    @MainActor
    private func handleOnAppear() async {
        #if DEBUG
        let cycle = await SettingsCycleCounter.shared.recordOpen()
        let tag = cycle.map { "cycle \($0)" } ?? "spurious re-fire"
        let t0 = MemoryPressureRelief.currentFootprintMB()
        memoryLogger.notice("settings \(tag, privacy: .public) onAppear entry: \(t0, privacy: .public)MB")
        #endif

        if SETTINGS_FLIP_ACTIVATION_POLICY {
            NSApplication.shared.setActivationPolicy(.regular)
        }

        #if DEBUG
        let t1 = MemoryPressureRelief.currentFootprintMB()
        if SETTINGS_FLIP_ACTIVATION_POLICY {
            memoryLogger.notice("settings \(tag, privacy: .public) after flip to .regular: \(t1, privacy: .public)MB (\(t1 - t0, privacy: .public)MB delta)")
        } else {
            memoryLogger.notice("settings \(tag, privacy: .public) flip disabled (condition A): \(t1, privacy: .public)MB")
        }
        try? await Task.sleep(for: .milliseconds(500))
        let t2 = MemoryPressureRelief.currentFootprintMB()
        memoryLogger.notice("settings \(tag, privacy: .public) 500ms post-render: \(t2, privacy: .public)MB (\(t2 - t1, privacy: .public)MB from flip)")
        #endif
    }

    @MainActor
    private func handleOnDisappear() async {
        #if DEBUG
        await SettingsCycleCounter.shared.recordClose()

        let t0 = MemoryPressureRelief.currentFootprintMB()
        memoryLogger.notice("settings onDisappear entry: \(t0, privacy: .public)MB")
        #endif

        if SETTINGS_FLIP_ACTIVATION_POLICY {
            NSApplication.shared.setActivationPolicy(.accessory)
        }

        #if DEBUG
        let t1 = MemoryPressureRelief.currentFootprintMB()
        if SETTINGS_FLIP_ACTIVATION_POLICY {
            memoryLogger.notice("settings after flip to .accessory: \(t1, privacy: .public)MB (\(t1 - t0, privacy: .public)MB delta)")
        }
        try? await Task.sleep(for: .seconds(3))
        let t2 = MemoryPressureRelief.currentFootprintMB()
        memoryLogger.notice("settings 3s post-close: \(t2, privacy: .public)MB (\(t2 - t1, privacy: .public)MB since flip)")

        MemoryPressureRelief.releaseReclaimablePages(reason: "settings close")

        let t3 = MemoryPressureRelief.currentFootprintMB()
        memoryLogger.notice("settings post-relief: \(t3, privacy: .public)MB (cycle residual vs onDisappear entry: \(t3 - t0, privacy: .public)MB)")
        #endif
    }
}

extension UserDefaults {
    func bool(forKey key: String, default defaultValue: Bool) -> Bool {
        object(forKey: key) == nil ? defaultValue : bool(forKey: key)
    }
}
