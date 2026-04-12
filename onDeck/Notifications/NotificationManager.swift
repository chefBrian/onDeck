import AppKit
import UserNotifications

// Stateless delegate - no mutable properties, so @unchecked is safe.
// NSObject doesn't conform to Sendable on its own in Swift 6.
final class NotificationDelegate: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    // Show notifications even when app is in the foreground
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }

    // Open click-through URL when notification is clicked, and remove from Notification Center
    func userNotificationCenter(_ center: UNUserNotificationCenter,
                                didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if let urlString = response.notification.request.content.userInfo["clickURL"] as? String,
           let url = URL(string: urlString) {
            NSWorkspace.shared.open(url)
        }
        center.removeDeliveredNotifications(withIdentifiers: [response.notification.request.identifier])
        completionHandler()
    }
}

/// Tracks pending auto-dismiss tasks so they can be coalesced and cancelled,
/// instead of leaking fire-and-forget `Task.detached` sleeps.
private actor DismissalBag {
    private var tasks: [String: Task<Void, Never>] = [:]

    func schedule(id: String, delay: TimeInterval) {
        tasks[id]?.cancel()
        tasks[id] = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            UNUserNotificationCenter.current().removeDeliveredNotifications(withIdentifiers: [id])
            await self?.clear(id: id)
        }
    }

    func cancelAll() {
        for task in tasks.values { task.cancel() }
        tasks.removeAll()
    }

    private func clear(id: String) {
        tasks.removeValue(forKey: id)
    }
}

final class NotificationManager: Sendable {
    static let shared = NotificationManager()
    private let delegate = NotificationDelegate()
    private let dismissalBag = DismissalBag()

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

    func send(title: String, body: String, identifier: String? = nil, playerID: Int? = nil, clickURL: URL? = nil, autoDismissAfter: TimeInterval? = nil) async {
        if MemDiagFlags.skipNotifications {
            print("[MemoryProbe] suppressing notification: \(title)")
            return
        }
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        guard settings.authorizationStatus == .authorized else {
            print("[Notifications] Not authorized (status: \(settings.authorizationStatus.rawValue))")
            return
        }

        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        if let clickURL {
            content.userInfo["clickURL"] = clickURL.absoluteString
        }
        if let playerID, let imageURL = HeadshotCache.shared.fileURL(for: playerID) {
            if let attachment = try? UNNotificationAttachment(identifier: "headshot", url: imageURL) {
                content.attachments = [attachment]
            }
        }

        let id = identifier ?? UUID().uuidString
        let request = UNNotificationRequest(identifier: id, content: content, trigger: nil)

        do {
            try await UNUserNotificationCenter.current().add(request)
            print("[Notifications] Sent: \(title) - \(body)")
            if let autoDismissAfter {
                await dismissalBag.schedule(id: id, delay: autoDismissAfter)
            }
        } catch {
            print("[Notifications] Failed to send: \(error)")
        }
    }

    /// Removes all delivered "not in lineup" notifications for the given game.
    func purgeNotInLineupNotifications(gamePk: Int) async {
        let prefix = "notInLineup-\(gamePk)-"
        let delivered = await UNUserNotificationCenter.current().deliveredNotifications()
        let ids = delivered.map(\.request.identifier).filter { $0.hasPrefix(prefix) }
        guard !ids.isEmpty else { return }
        UNUserNotificationCenter.current().removeDeliveredNotifications(withIdentifiers: ids)
        print("[Notifications] Purged \(ids.count) not-in-lineup notifications for game \(gamePk)")
    }

    /// Removes all delivered and pending notifications and cancels auto-dismiss timers.
    func purgeAll() async {
        let center = UNUserNotificationCenter.current()
        center.removeAllDeliveredNotifications()
        center.removeAllPendingNotificationRequests()
        await dismissalBag.cancelAll()
        print("[Notifications] Purged all notifications (day rollover)")
    }

    func purgeBatting(gamePk: Int, playerID: Int) {
        let id = "batting-\(gamePk)-\(playerID)"
        let center = UNUserNotificationCenter.current()
        center.removePendingNotificationRequests(withIdentifiers: [id])
        center.removeDeliveredNotifications(withIdentifiers: [id])
        print("[Notifications] Purge: \(id)")
    }

    func purgePitching(gamePk: Int, playerID: Int) {
        let id = "pitching-\(gamePk)-\(playerID)"
        let center = UNUserNotificationCenter.current()
        center.removePendingNotificationRequests(withIdentifiers: [id])
        center.removeDeliveredNotifications(withIdentifiers: [id])
        print("[Notifications] Purge: \(id)")
    }

    // MARK: - Typed Notifications

    func notifyBatting(playerName: String, playerID: Int, gamePk: Int, game: String, inning: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyBatting", default: true) else { return }
        await send(
            title: "\(playerName) is batting",
            body: "\(game), \(inning)",
            identifier: "batting-\(gamePk)-\(playerID)",
            playerID: playerID,
            clickURL: streamURL
        )
    }

    func notifyPitching(playerName: String, playerID: Int, gamePk: Int, game: String, inning: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitching", default: true) else { return }
        await send(
            title: "\(playerName) is taking the mound",
            body: "\(game), \(inning)",
            identifier: "pitching-\(gamePk)-\(playerID)",
            playerID: playerID,
            clickURL: streamURL
        )
    }

    func notifyAtBatResult(playerName: String, playerID: Int, description: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyAtBatResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description,
            playerID: playerID,
            clickURL: streamURL,
            autoDismissAfter: 30
        )
    }

    func notifyPitchingResult(playerName: String, playerID: Int, description: String, streamURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyPitchingResult", default: true) else { return }
        await send(
            title: "\(playerName)",
            body: description,
            playerID: playerID,
            clickURL: streamURL,
            autoDismissAfter: 30
        )
    }

    func notifyNotInLineup(playerName: String, playerID: Int, gamePk: Int, game: String, fantraxURL: URL?) async {
        guard UserDefaults.standard.bool(forKey: "notifyNotInLineup", default: true) else { return }
        await send(
            title: "\(playerName) is not in the lineup",
            body: game,
            identifier: "notInLineup-\(gamePk)-\(playerID)",
            playerID: playerID,
            clickURL: fantraxURL
        )
    }
}
