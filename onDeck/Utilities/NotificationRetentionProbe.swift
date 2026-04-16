#if DEBUG
import Foundation
import UserNotifications
import os.log

/// DEBUG-only diagnostic for the notification headshot retention hypothesis.
///
/// Sends N notifications with distinct cached headshots, waits for the system
/// to process attachments, purges them all, then snapshots `phys_footprint`
/// at four points:
///   1. Before any probe notifications exist (baseline)
///   2. After firing N with attachments (peak)
///   3. After purging all N (should drop if attachment bytes are releasable)
///   4. After `malloc_zone_pressure_relief` (catches allocator-retained slabs)
///
/// Interpretation:
///   - Peak rises then purge drops back near baseline → attachments release cleanly
///   - Peak rises, purge drops partially, relief drops further → allocator retention (Fix C helps)
///   - Peak rises, purge drops barely → UNNotification machinery retains bytes independently
///     (the only real fix is dropping per-player headshots for a shared icon)
enum NotificationRetentionProbe {

    private static let logger = Logger(subsystem: "dev.bjc.onDeck", category: "memory")

    /// Runs the probe using up to 50 cached headshots from the supplied roster IDs.
    static func run(rosterIDs: [Int]) async {
        let idsWithHeadshots = rosterIDs.filter { HeadshotCache.shared.fileURL(for: $0) != nil }
        let ids = Array(idsWithHeadshots.prefix(50))
        guard !ids.isEmpty else {
            logger.notice("probe aborted: no cached headshots (run a slate first so prefetch populates the cache)")
            return
        }

        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        guard settings.authorizationStatus == .authorized else {
            logger.notice("probe aborted: notification permission not granted")
            return
        }

        let probeIDs = ids.map { "probe-\($0)-\(UUID().uuidString.prefix(6))" }

        let baseline = MemoryPressureRelief.currentFootprintMB()
        logger.notice("probe start: firing \(ids.count, privacy: .public) notifications; footprint \(baseline, privacy: .public)MB")

        for (i, playerID) in ids.enumerated() {
            let content = UNMutableNotificationContent()
            content.title = "Probe \(i + 1)"
            content.body = "Retention diagnostic (safe to dismiss)"
            if let imageURL = HeadshotCache.shared.fileURL(for: playerID),
               let attachment = try? UNNotificationAttachment(identifier: "headshot", url: imageURL) {
                content.attachments = [attachment]
            }
            let request = UNNotificationRequest(identifier: probeIDs[i], content: content, trigger: nil)
            try? await center.add(request)
        }

        try? await Task.sleep(for: .seconds(3))
        let peak = MemoryPressureRelief.currentFootprintMB()
        logger.notice("probe peak: footprint \(peak, privacy: .public)MB (+\(peak - baseline, privacy: .public)MB from baseline)")

        center.removeDeliveredNotifications(withIdentifiers: probeIDs)
        center.removePendingNotificationRequests(withIdentifiers: probeIDs)

        try? await Task.sleep(for: .seconds(3))
        let afterPurge = MemoryPressureRelief.currentFootprintMB()
        logger.notice("probe after purge: footprint \(afterPurge, privacy: .public)MB (released \(peak - afterPurge, privacy: .public)MB since peak)")

        MemoryPressureRelief.releaseReclaimablePages(reason: "post-probe")

        let afterRelief = MemoryPressureRelief.currentFootprintMB()
        logger.notice("probe summary: baseline \(baseline, privacy: .public)MB / peak +\(peak - baseline, privacy: .public)MB / purge returned \(peak - afterPurge, privacy: .public)MB / relief returned \(afterPurge - afterRelief, privacy: .public)MB / residual \(afterRelief - baseline, privacy: .public)MB")
    }
}
#endif
