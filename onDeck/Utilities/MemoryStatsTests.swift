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

        test(&failures, "applySample ratchets maxBytes and never regresses when bytes drops") {
            let stats = MemoryStats()
            stats.applySample(bytes: 100)
            stats.applySample(bytes: 50)
            guard stats.maxBytes == 100 else {
                return "maxBytes regressed after lower sample: expected 100, got \(stats.maxBytes)"
            }
            guard stats.currentBytes == 50 else {
                return "currentBytes should reflect latest sample: expected 50, got \(stats.currentBytes)"
            }
            stats.applySample(bytes: 150)
            return stats.maxBytes == 150
                ? nil
                : "maxBytes failed to advance on higher sample: expected 150, got \(stats.maxBytes)"
        }

        if !failures.isEmpty {
            preconditionFailure("MemoryStatsTests failures:\n - " + failures.joined(separator: "\n - "))
        }
        print("[MemoryStatsTest] all tests passed")
    }

    private static func test(_ failures: inout [String], _ name: String, _ body: () -> String?) {
        if let reason = body() {
            failures.append("\(name): \(reason)")
        }
    }
}
#endif
