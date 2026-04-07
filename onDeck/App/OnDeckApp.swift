import SwiftUI

@main
struct OnDeckApp: App {
    @State private var appState = AppState()

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(appState: appState)
                .task { await appState.start() }
        } label: {
            Label(appState.menuBarTitle, systemImage: "baseball")
        }

        Settings {
            SettingsView(appState: appState)
        }
    }
}
