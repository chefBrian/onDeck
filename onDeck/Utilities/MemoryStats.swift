#if DEBUG
import Foundation
import Darwin

/// DEBUG-only memory-footprint HUD source. Polls `task_info(TASK_VM_INFO)`
/// at 1 Hz and tracks current + session-max `phys_footprint` (the same
/// metric Activity Monitor's "Memory" column surfaces on modern macOS).
///
/// `maxBytes` only ratchets upward on *sustained* readings — the trailing
/// window's minimum must exceed the current max. Transient spikes don't
/// inflate the high-water mark, which is what you want when eyeballing
/// steady-state memory pressure rather than hunting one-off allocation
/// peaks.
///
/// Wrapped entirely in `#if DEBUG` so the type - and all its storage and
/// the poll task - compiles out of Release builds.
@MainActor
@Observable
final class MemoryStats {
    /// Number of consecutive 1 Hz samples (≈ seconds) a reading must hold
    /// before `maxBytes` is allowed to ratchet up to it.
    static let sustainedSampleCount = 20

    private(set) var currentBytes: UInt64 = 0
    private(set) var maxBytes: UInt64 = 0

    private var recentSamples: [UInt64] = []
    private var pollTask: Task<Void, Never>?

    func start() {
        guard pollTask == nil else { return }
        sample()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                self?.sample()
            }
        }
    }

    deinit { MainActor.assumeIsolated { pollTask?.cancel() } }

    func sample() {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return }
        applySample(bytes: info.phys_footprint)
    }

    /// Applies an observed footprint reading to `currentBytes` and ratchets
    /// `maxBytes` upward only when the trailing window's minimum exceeds
    /// it — i.e. the reading has held for `sustainedSampleCount` samples.
    /// Split out from `sample()` so the ratchet can be exercised
    /// deterministically in `MemoryStatsTests` without having to predict
    /// how the kernel will report `phys_footprint` between calls.
    func applySample(bytes: UInt64) {
        currentBytes = bytes
        recentSamples.append(bytes)
        if recentSamples.count > Self.sustainedSampleCount {
            recentSamples.removeFirst()
        }
        let sustainedFloor = recentSamples.min() ?? bytes
        if sustainedFloor > maxBytes { maxBytes = sustainedFloor }
    }

    var currentMB: Int { Int(currentBytes / 1_048_576) }
    var maxMB: Int { Int(maxBytes / 1_048_576) }
}
#endif
