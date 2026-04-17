import SwiftUI

@main
struct OnDeckApp: App {
    @State private var appState = AppState()

    init() {
        #if DEBUG
        LiveFeedPatcherTests.runAll()
        #endif
    }

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(appState: appState)
        } label: {
            let isActive = !appState.activePlayers.isEmpty
            Label {
                Text(appState.menuBarTitle)
            } icon: {
                Image(systemName: "baseball")
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(isActive ? .green : .white)
            }
        }
        .menuBarExtraStyle(.window)

        Settings {
            SettingsView(appState: appState)
        }
    }
}
