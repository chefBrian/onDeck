import Foundation

@Observable
final class GameMonitor {
    var isConnected = false

    func startMonitoring(games: [Game]) async {
        // TODO: Open WebSocket to ws.statsapi.mlb.com, fall back to REST polling
    }

    func stopMonitoring() {
        // TODO: Close connections
    }
}
