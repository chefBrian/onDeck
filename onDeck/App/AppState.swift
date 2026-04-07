import SwiftUI

@Observable
final class AppState {
    var activePlayers: [Player] = []
    var upcomingPlayers: [Player] = []
    var inactivePlayers: [Player] = []
    var games: [Game] = []
    var rosterURL: String {
        get { UserDefaults.standard.string(forKey: "rosterURL") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "rosterURL") }
    }

    var menuBarTitle: String {
        let names = activePlayers.map(\.name)
        switch names.count {
        case 0: return ""
        case 1...3: return names.joined(separator: " | ")
        default: return names.prefix(3).joined(separator: " | ") + " +\(names.count - 3)"
        }
    }
}
