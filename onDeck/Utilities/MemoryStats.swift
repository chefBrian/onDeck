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
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<natural_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return }
        let footprint = info.phys_footprint
        currentBytes = footprint
        if footprint > maxBytes { maxBytes = footprint }
    }

    var currentMB: Int { Int(currentBytes / 1_048_576) }
    var maxMB: Int { Int(maxBytes / 1_048_576) }
}
#endif
