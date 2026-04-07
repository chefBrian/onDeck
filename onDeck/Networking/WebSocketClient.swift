import Foundation

final class WebSocketClient {
    private var webSocketTask: URLSessionWebSocketTask?

    func connect(gamePks: [Int]) async {
        // TODO: Connect to ws.statsapi.mlb.com, subscribe to game feeds
    }

    func disconnect() {
        webSocketTask?.cancel(with: .goingAway, reason: nil)
        webSocketTask = nil
    }
}
