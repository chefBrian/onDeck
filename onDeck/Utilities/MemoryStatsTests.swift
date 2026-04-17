#if DEBUG
import Foundation

/// DEBUG-only self-tests for `MemoryStats`. Invoked at app launch from
/// `OnDeckApp.init()`; failures trigger `preconditionFailure` so broken
/// behavior cannot ship.
///
/// The project has no XCTest target - matches the pattern used by
/// `LiveFeedPatcherTests`.
@MainActor
enum MemoryStatsTests {

    static func runAll() {
        var failures: [String] = []

        test(&failures, "sample() populates currentBytes with non-zero phys_footprint") {
            let stats = MemoryStats()
            stats.sample()
            return stats.currentBytes > 0
                ? nil
                : "currentBytes was 0 after sample() - task_info likely not wired up"
        }

        test(&failures, "after first sample, maxBytes equals currentBytes") {
            let stats = MemoryStats()
            stats.sample()
            return stats.maxBytes == stats.currentBytes
                ? nil
                : "maxBytes (\(stats.maxBytes)) != currentBytes (\(stats.currentBytes)) after single sample"
        }

        test(&failures, "maxBytes is monotonic across samples") {
            let stats = MemoryStats()
            stats.sample()
            let firstMax = stats.maxBytes
            // Allocate and drop ~4 MB to nudge phys_footprint upward. The max
            // must never regress regardless of whether the allocation stuck.
            autoreleasepool {
                _ = [UInt8](repeating: 0, count: 4 * 1_048_576)
            }
            stats.sample()
            return stats.maxBytes >= firstMax
                ? nil
                : "maxBytes regressed: \(firstMax) -> \(stats.maxBytes)"
        }

        if !failures.isEmpty {
            preconditionFailure("MemoryStatsTests failures:\n - " + failures.joined(separator: "\n - "))
        }
    }

    private static func test(_ failures: inout [String], _ name: String, _ body: () -> String?) {
        if let reason = body() {
            failures.append("\(name): \(reason)")
        }
    }
}
#endif
