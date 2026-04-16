#if DEBUG
import Foundation

/// Phase 1 instrumentation for the typed-patcher rewrite.
///
/// Writes every RFC 6902 op returned by `fetchDiffPatch` to a CSV in the
/// app's sandboxed Caches directory. Derives the empirical list of paths
/// MLB actually emits, so the typed patcher is built from real traffic
/// rather than inferred from our parser.
///
/// File: `~/Library/Containers/<bundle-id>/Data/Library/Caches/onDeck-diffpatch-trace.csv`
/// Columns: timestamp, gamePk, op, path, from, value_preview
///
/// Remove this file and its call site once the empirical path list is in hand.
final class DiffPatchTraceLogger: @unchecked Sendable {
    static let shared = DiffPatchTraceLogger()

    private let fileURL: URL
    private let lock = NSLock()
    private let maxBytes: Int = 10 * 1024 * 1024
    private var didWriteHeader = false

    private init() {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
        self.fileURL = caches.appendingPathComponent("onDeck-diffpatch-trace.csv")
        print("[DiffPatchTrace] logging to \(fileURL.path)")
    }

    func record(gamePk: Int, ops: [[String: Any]]) {
        guard !ops.isEmpty else { return }

        let timestamp = Self.isoFormatter.string(from: Date())
        var buffer = ""
        for op in ops {
            let opType = (op["op"] as? String) ?? ""
            let path = (op["path"] as? String) ?? ""
            let from = (op["from"] as? String) ?? ""
            let value = Self.previewValue(op["value"])
            buffer += "\(timestamp),\(gamePk),\(Self.escape(opType)),\(Self.escape(path)),\(Self.escape(from)),\(Self.escape(value))\n"
        }

        lock.lock()
        defer { lock.unlock() }
        ensureFileInitialized()
        rotateIfNeeded()
        append(buffer)
    }

    // MARK: - Private

    private func ensureFileInitialized() {
        if didWriteHeader { return }
        let fm = FileManager.default
        if !fm.fileExists(atPath: fileURL.path) {
            let header = "timestamp,gamePk,op,path,from,value_preview\n"
            fm.createFile(atPath: fileURL.path, contents: header.data(using: .utf8))
        }
        didWriteHeader = true
    }

    private func rotateIfNeeded() {
        let attrs = try? FileManager.default.attributesOfItem(atPath: fileURL.path)
        guard let size = attrs?[.size] as? Int, size >= maxBytes else { return }
        let rotated = fileURL.deletingPathExtension().appendingPathExtension("csv.1")
        try? FileManager.default.removeItem(at: rotated)
        try? FileManager.default.moveItem(at: fileURL, to: rotated)
        didWriteHeader = false
    }

    private func append(_ text: String) {
        guard let data = text.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: fileURL) {
            defer { try? handle.close() }
            try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        }
    }

    private static func previewValue(_ value: Any?) -> String {
        guard let value else { return "" }
        if value is NSNull { return "null" }

        let rendered: String
        if JSONSerialization.isValidJSONObject(value),
           let data = try? JSONSerialization.data(withJSONObject: value),
           let string = String(data: data, encoding: .utf8) {
            rendered = string
        } else if let string = value as? String {
            rendered = "\"\(string)\""
        } else {
            rendered = "\(value)"
        }
        return String(rendered.prefix(120))
    }

    private static func escape(_ s: String) -> String {
        guard s.contains(",") || s.contains("\"") || s.contains("\n") else { return s }
        let doubled = s.replacingOccurrences(of: "\"", with: "\"\"")
        return "\"\(doubled)\""
    }

    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}
#endif
