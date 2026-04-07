import UserNotifications

final class NotificationManager: Sendable {
    static let shared = NotificationManager()

    func requestPermission() async -> Bool {
        do {
            return try await UNUserNotificationCenter.current()
                .requestAuthorization(options: [.alert, .sound])
        } catch {
            return false
        }
    }

    func send(title: String, body: String, identifier: String? = nil) async {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default

        let id = identifier ?? UUID().uuidString
        let request = UNNotificationRequest(identifier: id, content: content, trigger: nil)

        try? await UNUserNotificationCenter.current().add(request)
    }

    // MARK: - Typed Notifications

    func notifyBatting(playerName: String, game: String, inning: String) async {
        guard UserDefaults.standard.bool(forKey: "notifyBatting", default: true) else { return }
        await send(
            title: "\(playerName) is batting",
            body: "\(game), \(inning)"
        )
    }

    func notifyPitching(playerName: String, game: String, inning: String) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitching", default: true) else { return }
        await send(
            title: "\(playerName) is taking the mound",
            body: "\(game), \(inning)"
        )
    }

    func notifyAtBatResult(playerName: String, description: String) async {
        guard UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description
        )
    }

    func notifyPitchingResult(playerName: String, description: String) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description
        )
    }
}
