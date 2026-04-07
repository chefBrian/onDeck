import Foundation

@Observable
final class RosterManager {
    var players: [Player] = []
    var lastSyncDate: Date?
    var error: String?

    func syncRoster(from url: String) async {
        // TODO: Fetch roster from Fantrax API and resolve MLB IDs
    }
}
