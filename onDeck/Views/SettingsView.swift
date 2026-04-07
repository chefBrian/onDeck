import SwiftUI

struct SettingsView: View {
    @State private var rosterURL = UserDefaults.standard.string(forKey: "rosterURL") ?? ""
    @State private var notifyBatting = UserDefaults.standard.bool(forKey: "notifyBatting", default: true)
    @State private var notifyPitching = UserDefaults.standard.bool(forKey: "notifyPitching", default: true)
    @State private var notifyAtBatResult = UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true)
    @State private var notifyPitchingResult = UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true)

    var body: some View {
        Form {
            Section("Fantrax Roster") {
                TextField("Roster URL", text: $rosterURL)
                    .onSubmit { UserDefaults.standard.set(rosterURL, forKey: "rosterURL") }
            }

            Section("Notifications") {
                Toggle("Stepping up to bat", isOn: $notifyBatting)
                Toggle("Taking the mound", isOn: $notifyPitching)
                Toggle("At-bat results", isOn: $notifyAtBatResult)
                Toggle("Pitching results", isOn: $notifyPitchingResult)
            }
        }
        .formStyle(.grouped)
        .frame(width: 400, height: 300)
    }
}

extension UserDefaults {
    func bool(forKey key: String, default defaultValue: Bool) -> Bool {
        object(forKey: key) == nil ? defaultValue : bool(forKey: key)
    }
}
