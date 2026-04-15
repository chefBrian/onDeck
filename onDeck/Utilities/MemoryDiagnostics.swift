import Foundation
import Darwin
import UserNotifications

enum MemoryDiagnostics {
    struct Snapshot {
        let rssBytes: UInt64
        let virtualBytes: UInt64
        let physFootprintBytes: UInt64   // matches Activity Monitor "Memory" column
        let compressedBytes: UInt64      // bytes swapped into the compressor
        let mallocInUseBytes: UInt64
        let mallocAllocatedBytes: UInt64
    }

    /// Reads the current process's memory footprint via Mach, plus
    /// default-zone malloc stats for attribution of heap growth.
    static func snapshot() -> Snapshot {
        // Basic info: resident_size + virtual_size
        var basic = mach_task_basic_info()
        var basicCount = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<integer_t>.size)
        let basicKr = withUnsafeMutablePointer(to: &basic) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(basicCount)) {
                task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &basicCount)
            }
        }
        let rss: UInt64 = basicKr == KERN_SUCCESS ? UInt64(basic.resident_size) : 0
        let vsize: UInt64 = basicKr == KERN_SUCCESS ? UInt64(basic.virtual_size) : 0

        // VM info: phys_footprint (matches Activity Monitor) + compressed
        var vmInfo = task_vm_info_data_t()
        var vmCount = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let vmKr = withUnsafeMutablePointer(to: &vmInfo) { ptr in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(vmCount)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &vmCount)
            }
        }
        let footprint: UInt64 = vmKr == KERN_SUCCESS ? UInt64(vmInfo.phys_footprint) : 0
        let compressed: UInt64 = vmKr == KERN_SUCCESS ? UInt64(vmInfo.compressed) : 0

        var stats = malloc_statistics_t()
        malloc_zone_statistics(nil, &stats)

        return Snapshot(
            rssBytes: rss,
            virtualBytes: vsize,
            physFootprintBytes: footprint,
            compressedBytes: compressed,
            mallocInUseBytes: UInt64(stats.size_in_use),
            mallocAllocatedBytes: UInt64(stats.size_allocated)
        )
    }
}

/// Tick-based CSV logger for memory probe runs. Opt-in via
/// `defaults write dev.bjc.onDeck memoryDiagnostics -bool YES`.
/// Writes one row per minute to `~/Library/Caches/onDeck-memlog.csv`
/// plus event rows on sleep/wake/flag changes.
@MainActor
final class MemoryProbeLogger {
    static let shared = MemoryProbeLogger()

    private var tickTask: Task<Void, Never>?
    private weak var appState: AppState?
    private let logURL: URL
    private var popoutOpenedAt: Date?
    private var popoutTotalSeconds: TimeInterval = 0
    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    private init() {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
        // v2: added phys_footprint + compressed columns. v1 file (if present)
        // is kept so prior baselines can still be read.
        logURL = dir.appendingPathComponent("onDeck-memlog-v2.csv")
    }

    var isEnabled: Bool {
        UserDefaults.standard.bool(forKey: "memoryDiagnostics")
    }

    func start(appState: AppState) {
        guard isEnabled else { return }
        guard tickTask == nil else { return }
        self.appState = appState
        writeHeaderIfNeeded()
        print("[MemoryProbe] Logging to \(logURL.path)")
        tickTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.logRow(event: "tick")
                try? await Task.sleep(for: .seconds(60))
            }
        }
        Task { await logRow(event: "start") }
    }

    func stop() {
        tickTask?.cancel()
        tickTask = nil
    }

    func logEvent(_ event: String) {
        guard isEnabled else { return }
        Task { await logRow(event: event) }
    }

    /// Logs `event` immediately, plus follow-up snapshots at +0.5s/+2s/+5s.
    /// Used to capture transient spikes around short-lived UI transitions
    /// (Settings open/close) that the 60s tick would otherwise miss.
    func logEventWithTrailingSnapshots(_ event: String) {
        guard isEnabled else { return }
        Task {
            await logRow(event: event)
            try? await Task.sleep(for: .milliseconds(500))
            await logRow(event: "\(event)+0.5s")
            try? await Task.sleep(for: .milliseconds(1500))
            await logRow(event: "\(event)+2s")
            try? await Task.sleep(for: .seconds(3))
            await logRow(event: "\(event)+5s")
        }
    }

    func notePopoutOpened() {
        popoutOpenedAt = Date()
        logEvent("popoutOpen")
    }

    func notePopoutClosed() {
        if let t = popoutOpenedAt {
            popoutTotalSeconds += Date().timeIntervalSince(t)
        }
        popoutOpenedAt = nil
        logEvent("popoutClose")
    }

    private func writeHeaderIfNeeded() {
        guard !FileManager.default.fileExists(atPath: logURL.path) else { return }
        let header = "timestamp,event,footprint_mb,rss_mb,compressed_mb,vsize_mb,malloc_inuse_mb,malloc_alloc_mb,active_games,monitored_games,latest_feeds,cached_feed_bytes,team_logos,notif_delivered,notif_pending,poll_count,popout_open,popout_total_s,f_noPopout,f_noNamespace,f_skipNotif,f_stubDecode,f_resetURLCache,url_cache_mem,url_cache_disk\n"
        FileManager.default.createFile(atPath: logURL.path, contents: header.data(using: .utf8))
    }

    private func logRow(event: String) async {
        guard isEnabled, let appState else { return }

        let snap = MemoryDiagnostics.snapshot()
        let report = appState.gameMonitor.memoryDiagnosticsReport()
        let center = UNUserNotificationCenter.current()
        let delivered = await center.deliveredNotifications().count
        let pending = await center.pendingNotificationRequests().count

        let defaults = UserDefaults.standard
        let popoutOpen = FloatingPanel.shared.isShowing
        let popoutLive = popoutOpenedAt.map { Int(Date().timeIntervalSince($0)) } ?? 0
        let popoutTotal = Int(popoutTotalSeconds) + popoutLive
        let teamLogos = TeamLogoCache.shared.memoryCount

        // Optional: flush URLCache each tick when running that ablation.
        if defaults.bool(forKey: "memDiagResetURLCache") {
            URLCache.shared.removeAllCachedResponses()
        }

        let urlMem = URLCache.shared.currentMemoryUsage
        let urlDisk = URLCache.shared.currentDiskUsage

        let fields: [String] = [
            Self.isoFormatter.string(from: Date()),
            event,
            mb(snap.physFootprintBytes),
            mb(snap.rssBytes),
            mb(snap.compressedBytes),
            mb(snap.virtualBytes),
            mb(snap.mallocInUseBytes),
            mb(snap.mallocAllocatedBytes),
            String(report.activeGames),
            String(report.monitoredGames),
            String(report.latestFeeds),
            String(report.cachedFeedBytes),
            String(teamLogos),
            String(delivered),
            String(pending),
            String(report.pollCount),
            popoutOpen ? "1" : "0",
            String(popoutTotal),
            flag("memDiagNoPopout"),
            flag("memDiagNoNamespace"),
            flag("memDiagSkipNotifications"),
            flag("memDiagStubFeedDecode"),
            flag("memDiagResetURLCache"),
            String(urlMem),
            String(urlDisk),
        ]
        let row = fields.joined(separator: ",") + "\n"

        if let handle = try? FileHandle(forWritingTo: logURL) {
            defer { try? handle.close() }
            try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(row.utf8))
        }
    }

    private func mb(_ bytes: UInt64) -> String {
        String(format: "%.2f", Double(bytes) / 1024.0 / 1024.0)
    }

    private func flag(_ key: String) -> String {
        UserDefaults.standard.bool(forKey: key) ? "1" : "0"
    }
}

/// Diagnostic flags read at runtime via UserDefaults.
/// Flip via `defaults write dev.bjc.onDeck <key> -bool YES/NO`.
enum MemDiagFlags {
    static var skipNotifications: Bool {
        UserDefaults.standard.bool(forKey: "memDiagSkipNotifications")
    }
    static var stubFeedDecode: Bool {
        UserDefaults.standard.bool(forKey: "memDiagStubFeedDecode")
    }
    static var noNamespace: Bool {
        UserDefaults.standard.bool(forKey: "memDiagNoNamespace")
    }
    static var noPopout: Bool {
        UserDefaults.standard.bool(forKey: "memDiagNoPopout")
    }
}
