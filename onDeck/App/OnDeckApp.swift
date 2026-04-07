import SwiftUI

@main
struct OnDeckApp: App {
    @State private var appState = AppState()

    var body: some Scene {
        MenuBarExtra {
            MenuBarView(appState: appState)
        } label: {
            Label(appState.menuBarTitle, systemImage: "baseball")
        }
    }
}
