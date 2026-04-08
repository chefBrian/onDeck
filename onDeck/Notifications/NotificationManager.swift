import AppKit
import UserNotifications

final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate, Sendable {
    // Show notifications even when app is in the foreground
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    // Open stream URL when notification is clicked
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if let urlString = response.notification.request.content.userInfo["streamURL"] as? String,
           let url = URL(string: urlString) {
            NSWorkspace.shared.open(url)
        }
        completionHandler()
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

    func send(title: String, body: String, identifier: String? = nil, playerID: Int? = nil, streamURL: URL? = nil) async {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        guard settings.authorizationStatus == .authorized else {
            print("[Notifications] Not authorized (status: \(settings.authorizationStatus.rawValue))")
            return
        }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        if let streamURL {
            content.userInfo["streamURL"] = streamURL.absoluteString
        }
        if let playerID, let imageURL = await HeadshotCache.shared.fileURL(for: playerID) {
            if let attachment = try? UNNotificationAttachment(identifier: "headshot", url: imageURL) {
                content.attachments = [attachment]
            }
        }

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

    func notifyBatting(playerName: String, playerID: Int, game: String, inning: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyBatting", default: true) else { return }
        await send(
            title: "\(playerName) is batting",
            body: "\(game), \(inning)",
            playerID: playerID,
            streamURL: streamURL
        )
    }

    func notifyPitching(playerName: String, playerID: Int, game: String, inning: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitching", default: true) else { return }
        await send(
            title: "\(playerName) is taking the mound",
            body: "\(game), \(inning)",
            playerID: playerID,
            streamURL: streamURL
        )
    }

    func notifyAtBatResult(playerName: String, playerID: Int, description: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description,
            playerID: playerID,
            streamURL: streamURL
        )
    }

    func notifyPitchingResult(playerName: String, playerID: Int, description: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description,
            playerID: playerID,
            streamURL: streamURL
        )
    }
}
