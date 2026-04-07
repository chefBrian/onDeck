import UserNotifications

final class NotificationManager {
    static let shared = NotificationManager()

    func requestPermission() async -> Bool {
        do {
            return try await UNUserNotificationCenter.current()
                .requestAuthorization(options: [.alert, .sound])
        } catch {
            return false
        }
    }

    func send(title: String, body: String) async {
        // TODO: Create and deliver UNNotificationRequest
    }
}
