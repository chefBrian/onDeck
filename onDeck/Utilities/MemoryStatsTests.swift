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

        test(&failures, "maxBytes never regresses when bytes drops") {
            let stats = MemoryStats()
            stats.applySample(bytes: 100)
            stats.applySample(bytes: 50)
            guard stats.maxBytes == 100 else {
                return "maxBytes regressed after lower sample: expected 100, got \(stats.maxBytes)"
            }
            return stats.currentBytes == 50
                ? nil
                : "currentBytes should reflect latest sample: expected 50, got \(stats.currentBytes)"
        }

        test(&failures, "transient single-sample spike does not ratchet maxBytes") {
            let stats = MemoryStats()
            stats.applySample(bytes: 100)
            stats.applySample(bytes: 500) // spike
            stats.applySample(bytes: 100)
            return stats.maxBytes == 100
                ? nil
                : "spike inflated maxBytes: expected 100, got \(stats.maxBytes)"
        }

        test(&failures, "sustained high reading across full window raises maxBytes") {
            let stats = MemoryStats()
            for _ in 0..<MemoryStats.sustainedSampleCount {
                stats.applySample(bytes: 300)
            }
            return stats.maxBytes == 300
                ? nil
                : "maxBytes failed to ratchet after a full window of 300: got \(stats.maxBytes)"
        }

        test(&failures, "dip inside window blocks ratchet until dip ages out") {
            let stats = MemoryStats()
            stats.applySample(bytes: 100) // dip — oldest slot
            for _ in 0..<(MemoryStats.sustainedSampleCount - 1) {
                stats.applySample(bytes: 300)
            }
            // Window is full and still holds the 100 as its minimum.
            guard stats.maxBytes == 100 else {
                return "maxBytes advanced while dip still in window: got \(stats.maxBytes)"
            }
            stats.applySample(bytes: 300) // evicts the 100
            return stats.maxBytes == 300
                ? nil
                : "maxBytes failed to ratchet after dip aged out: got \(stats.maxBytes)"
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
