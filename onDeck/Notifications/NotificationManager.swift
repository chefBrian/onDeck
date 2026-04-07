import UserNotifications

final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate, Sendable {
    // Show notifications even when app is in the foreground
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}

final class NotificationManager: Sendable {
    static let shared = NotificationManager()
    private let delegate = NotificationDelegate()

    init() {
        UNUserNotificationCenter.current().delegate = delegate
    }

    func requestPermission() async -> Bool {
        do {
            let granted = try await UNUserNotificationCenter.current()
                .requestAuthorization(options: [.alert, .sound, .badge])
            print("[Notifications] Permission granted: \(granted)")
            return granted
        } catch {
            print("[Notifications] Permission error: \(error)")
            return false
        }
    }

    func send(title: String, body: String, identifier: String? = nil) async {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        guard settings.authorizationStatus == .authorized else {
            print("[Notifications] Not authorized (status: \(settings.authorizationStatus.rawValue))")
            return
        }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default

        let id = identifier ?? UUID().uuidString
        let request = UNNotificationRequest(identifier: id, content: content, trigger: nil)

        do {
            try await UNUserNotificationCenter.current().add(request)
            print("[Notifications] Sent: \(title) - \(body)")
        } catch {
            print("[Notifications] Failed to send: \(error)")
        }
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
