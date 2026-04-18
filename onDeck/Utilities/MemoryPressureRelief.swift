import Darwin
import Foundation

/// Releases memory that's only retained because nothing has asked the allocator
/// (or app-level caches) to let go. Two tiers:
///
/// 1. App-level caches we know are safe to drop at idle — the shared `URLCache`
///    (non-MLB traffic still writes to it: Fantrax roster, headshots, logos) and
///    the in-memory `TeamLogoCache` layer (PNGs stay on disk, reload in ~1 ms).
/// 2. libmalloc free-list pages via `malloc_zone_pressure_relief`. macOS
///    allocators park freed pages in per-zone free lists rather than returning
///    them via `madvise(MADV_FREE)` — giving them back costs a page fault on
///    the next alloc, so the allocator keeps them for reuse until the OS
///    signals pressure. On a well-provisioned Mac the OS never does, and
///    `phys_footprint` stays close to the peak indefinitely.
///
/// Call at idle transitions where we know no burst is coming imminently:
///   - last game of the slate ends
///   - system wakes from sleep (next to timecode invalidation)
///   - day rollover / full stopMonitoring
///
/// NOT per-game mid-slate: the allocator would re-grow for the next poll cycle and we'd
/// pay the syscall cost for nothing.
@MainActor
enum MemoryPressureRelief {
    static func releaseReclaimablePages() {
        URLCache.shared.removeAllCachedResponses()
        TeamLogoCache.shared.evictMemoryCache()
        _ = malloc_zone_pressure_relief(nil, 0)
    }
}
