import Darwin
import Foundation

/// Asks libmalloc to return reclaimable pages to the OS.
///
/// macOS allocators park freed pages in per-zone free lists rather than returning them
/// via `madvise(MADV_FREE)` - giving them back costs a page fault on the next alloc,
/// so the allocator keeps them for reuse until the OS signals pressure. On a well-
/// provisioned Mac the OS never does, and `phys_footprint` stays close to the peak
/// indefinitely.
///
/// Call at idle transitions where we know no burst is coming imminently:
///   - last game of the slate ends
///   - system wakes from sleep (next to timecode invalidation)
///   - day rollover / full stopMonitoring
///
/// NOT per-game mid-slate: the allocator would re-grow for the next poll cycle and we'd
/// pay the syscall cost for nothing.
enum MemoryPressureRelief {

    static func releaseReclaimablePages(reason: String) {
        let before = currentFootprintMB()
        let released = malloc_zone_pressure_relief(nil, 0)
        let after = currentFootprintMB()
        print("[MemoryRelief] \(reason): released \(released / 1024 / 1024)MB; footprint \(before)MB -> \(after)MB")
    }

    private static func currentFootprintMB() -> Int {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return -1 }
        return Int(info.phys_footprint) / 1024 / 1024
    }
}
