#if DEBUG
import Foundation
import Darwin

/// DEBUG-only memory-footprint HUD source. Polls `task_info(TASK_VM_INFO)`
/// at 1 Hz and tracks current + session-max `phys_footprint` (the same
/// metric Activity Monitor's "Memory" column surfaces on modern macOS).
///
/// Wrapped entirely in `#if DEBUG` so the type - and all its storage and
/// the poll task - compiles out of Release builds.
@MainActor
@Observable
final class MemoryStats {
    private(set) var currentBytes: UInt64 = 0
    private(set) var maxBytes: UInt64 = 0

    private var pollTask: Task<Void, Never>?

    func start() {
        // Stub - implemented in Task 4.
    }

    func sample() {
        // Stub - implemented in Task 3. Intentionally no-op so the
        // self-tests in MemoryStatsTests fail until real logic lands.
    }

    var currentMB: Int { Int(currentBytes / 1_048_576) }
    var maxMB: Int { Int(maxBytes / 1_048_576) }
}
#endif
