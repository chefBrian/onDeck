import SwiftUI

struct SettingsView: View {
    @Bindable var appState: AppState
    @State private var notifyBatting = UserDefaults.standard.bool(forKey: "notifyBatting", default: true)
    @State private var notifyPitching = UserDefaults.standard.bool(forKey: "notifyPitching", default: true)
    @State private var notifyAtBatResult = UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true)
    @State private var notifyPitchingResult = UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true)

    var body: some View {
        Form {
            Section("Fantrax Roster") {
                TextField("Roster URL", text: $appState.rosterURL)
                    .onSubmit {
                        Task { await appState.resyncRoster() }
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
                    .disabled(appState.rosterManager.isSyncing || appState.rosterURL.isEmpty)
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
            }
        }
        .formStyle(.grouped)
        .frame(width: 450, height: 350)
    }
}

extension UserDefaults {
    func bool(forKey key: String, default defaultValue: Bool) -> Bool {
        object(forKey: key) == nil ? defaultValue : bool(forKey: key)
    }
}
