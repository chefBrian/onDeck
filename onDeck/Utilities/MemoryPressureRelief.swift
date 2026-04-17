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
    static func releaseReclaimablePages() {
        _ = malloc_zone_pressure_relief(nil, 0)
    }
}
