import Foundation

/// Permanent log of RFC 6902 ops the typed patcher has no handler for.
///
/// Writes to `~/Library/Containers/<bundle-id>/Data/Library/Caches/onDeck-unknown-patches.csv`
/// with a 10 MB rotation cap. Console output prefixed `[LiveFeedPatcher] unknown: …`.
///
/// Reviewed periodically - any frequently-seen path on a field we care about
/// becomes a new patcher handler in a follow-up round.
final class UnknownPatchLogger: @unchecked Sendable {
    static let shared = UnknownPatchLogger()

    private let fileURL: URL
    private let lock = NSLock()
    private let maxBytes: Int = 10 * 1024 * 1024
    private var didWriteHeader = false

    private init() {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
        self.fileURL = caches.appendingPathComponent("onDeck-unknown-patches.csv")
    }

    func record(op: String, path: String, from: String?, value: Any?) {
        let timestamp = Self.isoFormatter.string(from: Date())
        let fromField = from ?? ""
        let preview = Self.previewValue(value)
        let row = "\(timestamp),\(Self.escape(op)),\(Self.escape(path)),\(Self.escape(fromField)),\(Self.escape(preview))\n"

        print("[LiveFeedPatcher] unknown: \(op) \(path)\(from.map { " from=\($0)" } ?? "")")

        lock.lock()
        defer { lock.unlock() }
        ensureFileInitialized()
        rotateIfNeeded()
        append(row)
    }

    private func ensureFileInitialized() {
        if didWriteHeader { return }
        let fm = FileManager.default
        if !fm.fileExists(atPath: fileURL.path) {
            let header = "timestamp,op,path,from,value_preview\n"
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
