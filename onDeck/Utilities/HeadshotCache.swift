import AppKit

@MainActor
final class HeadshotCache {
    static let shared = HeadshotCache()

    private var memory: [Int: NSImage] = [:]
    private let cacheDir: URL = {
        let dir = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Headshots", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    /// Returns the on-disk file URL for a player's headshot, or nil if not cached.
    func fileURL(for playerID: Int) -> URL? {
        let file = cacheDir.appendingPathComponent("\(playerID).png")
        return FileManager.default.fileExists(atPath: file.path) ? file : nil
    }

    /// Prefetch headshots for all players, skipping any already on disk.
    func prefetch(playerIDs: [Int]) async {
        await withTaskGroup(of: Void.self) { group in
            for id in playerIDs {
                let file = cacheDir.appendingPathComponent("\(id).png")
                guard !FileManager.default.fileExists(atPath: file.path) else { continue }
                group.addTask {
                    await self.download(playerID: id)
                }
            }
        }
    }

    private func download(playerID: Int) async {
        guard let url = URL(string: "https://img.mlbstatic.com/mlb-photos/image/upload/d_people:generic:headshot:67:current.png/w_128/q_auto:best/v1/people/\(playerID)/headshot/67/current") else { return }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            guard let image = NSImage(data: data) else { return }
            let file = cacheDir.appendingPathComponent("\(playerID).png")
            try? data.write(to: file)
            await MainActor.run { memory[playerID] = image }
        } catch {
            // Silently skip - notification will just have no image
        }
    }
}
