# On Deck — Memory Investigation Findings

**Branch**: `memory-probe-2` • **Period**: 2026-04-12 through 2026-04-14 • **Status**: Diagnosis complete, fixes not yet implemented

---

## Executive summary

On Deck drifts from ~50 MB at launch to ~260 MB during a full MLB game slate. A prior investigation ([docs/memory-investigation.md](../docs/memory-investigation.md)) concluded this was a bounded ~190 MB plateau from allocator high-water behavior. **That conclusion was wrong.** Observations on 2026-04-12 showed a sustained 263 MB reading with only 2 active games remaining, and a full reset to 50 MB on relaunch.

A two-day instrumented probe on branch `memory-probe-2` isolated three independent sources of memory growth during live game slates:

| Cause | Mechanism | Rate (10 active games) | % of growth |
|---|---|---|---|
| **JSON parsing** | `JSONDecoder.decode(LiveFeedResponse.self)` on 500-800 KB feeds × N games × 6/min | **~26 MB/hr heap** | ~55% |
| **Remaining JSON work** | `JSONSerialization.jsonObject` + `JSONPatch.apply` + `JSONSerialization.data` | ~10 MB/hr heap | ~20% |
| **Notifications** | `UNNotificationAttachment` with headshot images | ~7 MB/hr non-heap | ~15% |
| **Popout rendering** | SwiftUI view tree + matchedGeometryEffect + Observation | ~2-5 MB/hr non-heap | ~10% |

The problem compounds because **macOS `malloc` never returns pages to the OS on its own** — once the heap grows to accommodate peak parse activity, it stays at that size for the life of the process. This is why restarting the app cleanly resets footprint to 50 MB, but letting it sit idle overnight does not.

A separate Settings-open UI transient spikes the footprint by +230 MB for 1-2 seconds but releases reliably. Not a contributor to end-of-day growth; cosmetic only.

---

## Diagnosis methodology

A lightweight probe harness was added to `memory-probe-2`:

- **`MemoryProbeLogger`** ([onDeck/Utilities/MemoryDiagnostics.swift](../onDeck/Utilities/MemoryDiagnostics.swift)) writes a CSV row to `~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv` every 60 seconds plus event rows on sleep/wake, popout open/close, Settings open/close.
- **Primary metric**: `phys_footprint` via `task_info(TASK_VM_INFO)` — matches Activity Monitor's "Memory" column. The earlier `resident_size` reading overstated by ~110 MB of shared framework pages.
- **Secondary metrics**: `malloc_inuse` and `malloc_alloc` via `malloc_zone_statistics`, per-game caches via `GameMonitor.memoryDiagnosticsReport()`, URLCache memory/disk, UNNotificationCenter pending/delivered counts.
- **Runtime ablation flags** (via UserDefaults on `dev.bjc.onDeck.debug` domain):
  - `memDiagNoPopout` — suppresses popout, closes existing
  - `memDiagNoNamespace` — omits `matchedGeometryEffect`
  - `memDiagSkipNotifications` — early-returns in `NotificationManager.send`
  - `memDiagStubFeedDecode` — returns empty `LiveFeedData` from `MLBStatsAPI.decodeLiveFeed`
  - `memDiagResetURLCache` — not run (URLCache was 30-450 KB throughout, non-factor)
  - `memDiagNoActivationPolicy` — not a factor (Settings spike was identical with flag on/off)

The harness is isolated to the `memory-probe-2` branch and not merged to `main`.

Logs preserved in [`logs/`](logs/):
- `probe-overnight-2026-04-13.csv` — first overnight, quick partial
- `probe-day1-2026-04-13.csv` — full day 1 probe with 5 flag windows
- `probe-day2-2026-04-14.csv` — day 2 probe including the stub-decode test

---

## Day 1 (2026-04-13) — flag ablation windows

Each window: 90 minutes nominal. Schedule flexed because user joined mid-afternoon; total game count climbed from 1 → 6 during the probe, which confounds absolute slope comparisons but not relative attribution.

| Window | Duration | Popout | Flag | footprint Δ | heap Δ | non-heap Δ | non-heap/hr |
|---|---|---|---|---|---|---|---|
| Baseline | 57 min | open | none | +15.24 | +6.40 | **+8.84** | **+9.17** |
| noPopout | 80 min | **closed** | `memDiagNoPopout` | -5.02 | +6.33 | -11.35 | -8.34 |
| noNamespace | 119 min | open (no matched-geom) | `memDiagNoNamespace` | +74.54 | +61.24 | +13.30 | +6.72 |
| skipNotif | 131 min | open | `memDiagSkipNotifications` | +46.25 | +42.20 | +4.05 | **+1.85** |

Key inferences:

- **Popout attribution**: `noPopout` inverted non-heap growth to *negative* (-8.34 MB/hr) — framework memory was actively being freed. Baseline's +9.17 MB/hr of non-heap is ~100% attributable to popout-side activity (UI rendering, image caches, notifications driven by state changes, SwiftUI graph state).

- **Namespace attribution**: only cut non-heap rate from ~9.2 → ~6.7 MB/hr. `matchedGeometryEffect`/`@Namespace` contributes ~2.5 MB/hr — small but non-zero.

- **Notifications attribution**: dropped non-heap rate from ~9.2 → ~1.85 MB/hr. **Notifications account for ~80% of non-heap growth during active games.** Specifically, `UNNotificationAttachment` with headshot image files in [NotificationManager.swift:87-91](../onDeck/Notifications/NotificationManager.swift#L87-L91).

- **Heap growth scales with game count**, not with popout/namespace/notification state. In every window, heap grew ~5-7 MB/hr per active game, consistent with Foundation JSON parser allocator pressure.

Reference rows in [`logs/probe-day1-2026-04-13.csv`](logs/probe-day1-2026-04-13.csv):
- Baseline: `2026-04-13T20:47:10Z` through `2026-04-13T21:44:22Z`
- noPopout: `2026-04-13T21:45:22Z` through `2026-04-13T23:07:07Z`
- noNamespace: `2026-04-13T23:08:07Z` through `2026-04-14T01:06:52Z`
- skipNotif: `2026-04-14T01:07:55Z` through `2026-04-14T03:19:07Z`

### Settings spike (day 1 pre-flight)

Before the main schedule, a 6-cycle A/B isolated the Settings-open spike. Phase A had `setActivationPolicy` ON (default), Phase B had `memDiagNoActivationPolicy` ON suppressing the toggle.

Observed pattern, both phases identical:
- Every `settingsOpen` and `settingsClose` fired a spike from baseline → ~281 MB (Phase A) or ~288 MB (Phase B)
- Peak held for ~0.5 s, released to baseline within 2 s
- `malloc_inuse` flat throughout — **pure non-heap framework memory, zero heap contribution**
- `malloc_alloc` grew ~1 MB per open cycle — small persistent residue

Conclusion: activation policy is not the cause. Something in SwiftUI `Settings {}` scene construction, `Form.formStyle(.grouped)`, or `NSHostingView` wrapping is loading ~230 MB of framework memory on demand. Releases reliably; cumulative impact ~13 MB if user opens Settings 20 times per day. Cosmetic only, not a fix priority.

Reference rows in day 1 CSV: search for `settingsOpen` / `settingsClose` events around `2026-04-13T20:35` and `2026-04-13T20:45`.

### Day 1 end-state observation

At session end (2026-04-14T03:19:07Z), with 2 active games still running:
```
footprint=192.06 MB  malloc_inuse=148.73 MB  malloc_alloc=248.00 MB  active=2
```

77% of footprint was live heap — consistent with the "JSON parser high-water mark" hypothesis.

---

## Overnight (2026-04-14 00:00 – 12:00) — malloc retention confirmed

After last game ended at ~04:54 UTC, polls froze at 2567. **Footprint held flat at 201 MB for 7+ hours** of pure idle:

```
04:54 footprint=201.13  malloc_inuse=154.32  malloc_alloc=260.00  active=0  polls=2567
05:25 footprint=201.08  malloc_inuse=154.32  malloc_alloc=260.00  active=0  polls=2567
...
12:02 footprint=201.50  malloc_inuse=155.55  malloc_alloc=260.00  active=0  polls=2567
```

This isolated the macOS `malloc` retention behavior: once pages are mapped into the heap, they remain mapped until:
- process terminates, or
- memory compressor kicks in under pressure, or
- we explicitly call `malloc_zone_pressure_relief()`

**The "leak" is not a conventional leak.** It's transient JSON allocations never being released back to the OS after they're freed within the process. The yesterday-to-today persistence of 200+ MB is this exact behavior.

Reference rows in [`logs/probe-day2-2026-04-14.csv`](logs/probe-day2-2026-04-14.csv): timestamps `2026-04-14T04:54` through `2026-04-14T12:02`.

---

## Day 2 (2026-04-14) — stub-decode test isolates JSON parser

On a fresh restart (footprint=15.77 MB at `2026-04-14T22:26:09Z`), polled through a full 10-game slate. Two windows:

| Window | Duration | Games | footprint slope | **heap slope** | non-heap slope |
|---|---|---|---|---|---|
| Pre-flag baseline | 80 min | 0→10 | 46.93 MB/hr | **36.16 MB/hr** | 10.77 MB/hr |
| stubDecode ON | 84 min | 10-11 | 16.33 MB/hr | **9.66 MB/hr** | 6.67 MB/hr |

**Codable `LiveFeedResponse` decode accounts for ~73% of heap growth** during active games — ~26 MB/hr eliminated by stubbing that single call site ([MLBStatsAPI.swift:81-84](../onDeck/Networking/MLBStatsAPI.swift#L81-L84)).

Remaining ~10 MB/hr of heap growth comes from the JSON pipeline steps the stub did NOT bypass:
- `JSONSerialization.jsonObject(with: cachedData)` ([GameMonitor.swift:211](../onDeck/Managers/GameMonitor.swift#L211))
- `JSONPatch.apply(patches, to: &json)` ([GameMonitor.swift:212](../onDeck/Managers/GameMonitor.swift#L212))
- `JSONSerialization.data(withJSONObject: json)` ([GameMonitor.swift:213](../onDeck/Managers/GameMonitor.swift#L213))

The incremental-parse plan at `docs/incremental-feed-parse.md` eliminates all four JSON steps (stub-bypassed + not-stub-bypassed), so its expected heap impact is larger than just the ~26 MB/hr we measured.

### Recovery window observation

After clearing `memDiagStubFeedDecode` at `2026-04-15T01:14:27Z`, heap went flat — decode activity resumed but heap stayed at 72 MB because:
1. Active game count dropped 11 → 9 (slate winding down)
2. Allocator high-water had already been established during the stub window

This illuminates an important nuance: **heap growth is not linear per poll**. Foundation's malloc reserves its working set at peak concurrent demand. Once established, routine parsing reuses free pages rather than expanding. New peaks (more games, larger feeds) push the high-water higher; nothing shrinks it.

Reference rows: `2026-04-14T22:26` – `2026-04-15T01:14` (pre-flag + stub + recovery).

---

## Root cause: two independent problems

### Problem 1 — JSON parser allocation amplitude (heap)

For every poll cycle, every active game, the current code executes this chain:

```swift
// GameMonitor.pollSingleGame, line 198-233
var json = try JSONSerialization.jsonObject(with: cachedData)     // Data → [String: Any]
try JSONPatch.apply(patches, to: &json)                           // mutate [String: Any]
let newData = try JSONSerialization.data(withJSONObject: json)    // [String: Any] → Data
let (decoded, newTimecode) = try MLBStatsAPI.decodeLiveFeed(from: newData)  // Data → LiveFeedData via Codable
```

Each step allocates transient Foundation objects totaling megabytes per game. At peak (10 games × 6 cycles/min), this hits the allocator hard. When the transients are freed, `malloc` keeps the pages on free lists. The allocator's working set ratchets up to match peak concurrent demand and never shrinks.

**This is the dominant cause of end-of-day footprint.** Fixing it is the incremental-parse refactor already designed at `docs/incremental-feed-parse.md`.

### Problem 2 — Malloc retention after allocation (whole-process)

macOS `malloc` (default `libmalloc` zone) policy:
- `free()` returns the block to the zone's free list
- The free list pages stay resident (in our process, counted in `malloc_alloc` and `phys_footprint`)
- Only pressure events (compressor under load, explicit `malloc_zone_pressure_relief` call, or process exit) release pages back to the OS
- At 200 MB+ allocated from the OS and a system with free RAM, there's no pressure and no release

**This makes Problem 1 worse** because the allocator high-water never comes down. If we fix Problem 1's amplitude, we don't need to explicitly fix Problem 2 — the high-water will simply never rise high enough to be noticeable. But a belt-and-suspenders call to `malloc_zone_pressure_relief(nil, 0)` after game-end cleanup would guarantee release.

### Secondary problems (non-heap)

- **Notifications with headshot attachments** — ~7 MB/hr non-heap growth during games. Each `UNNotificationAttachment` from a file URL loads NSImage decode buffers and attachment metadata that Foundation retains in our process. Fix scope: either drop image attachments, use pre-decoded thumbnails, or send text-only notifications.

- **SwiftUI popout intrinsic cost** — ~2-5 MB/hr non-heap. View graph state, CoreAnimation layers, `matchedGeometryEffect` bookkeeping. Not easily fixable without a UI rewrite; acceptable trade-off.

- **Settings scene transient spike** — +230 MB for 1-2 seconds per open. Frustrating but releases reliably. Deferred.

---

## Attribution budget (projected end-of-day, 10-game slate)

| Contributor | Current | After JSON fix | After JSON + notif fix |
|---|---|---|---|
| Heap (JSON parse + Codable) | ~300 MB peak | **~30 MB** | ~30 MB |
| Non-heap (notifications) | ~70 MB | ~70 MB | **~5 MB** |
| Non-heap (popout) | ~20 MB | ~20 MB | ~20 MB |
| Non-heap (Swift/Foundation overhead) | ~30 MB | ~30 MB | ~30 MB |
| **phys_footprint peak** | **~260 MB** | **~100-120 MB** | **~70-90 MB** |
| **phys_footprint idle overnight** | ~200 MB | **~60 MB** | **~55 MB** |

These estimates assume:
- Heap amplitude stays small enough that `malloc` never reserves large free lists
- Pressure-relief calls trigger on game-end and system-resume events
- Popout is open throughout the slate (accepting its intrinsic cost)

---

## Open questions and caveats

- **We never ran `memDiagResetURLCache`**. URLCache memory was 30-450 KB throughout, disk auto-purges. If URL caching turns out to contribute after other fixes are in, it'll surface then.

- **Popout intrinsic cost is not fully decomposed.** `noPopout` showed negative non-heap growth (framework releasing), but that's a conflation of "popout rendering stops" and "fewer state changes driving UI updates." Exact mechanism (SwiftUI view graph retention vs CoreAnimation layers vs NSHostingView caching) is unknown. Not worth untangling unless post-fix footprint is still unacceptable.

- **Settings spike internal cause is unknown.** Identified as non-heap framework memory that releases reliably. Instruments Allocations would name the responsible class but we haven't run it. Low-priority because it doesn't contribute to steady-state.

- **Heap growth attribution within the 9.66 MB/hr stub-decode rate is inferential.** We know the Codable call was bypassed; we know the JSONSerialization + patch + serialize calls were NOT bypassed. We haven't stubbed those individually to quantify each. The incremental-parse plan replaces all four, so the distinction doesn't affect fix design.

- **Every measurement is post-fact**. Data was collected by a CSV logger sampling every 60 s. Short-duration spikes (like Settings) were caught only because we added trailing +0.5s/+2s/+5s snapshots on known events. Any other sub-minute spike is invisible in this data.

---

## References

### Commits (branch `memory-probe-2`)
- `50d65e3` — `chore: log phys_footprint + compressed in memlog-v2.csv` — added the metric that matches Activity Monitor
- `ef85354` — `chore: add memory probe harness with CSV logger and ablation flags` — initial harness
- `0b2bc99` — `fix: purge all notifications on day rollover` — merged to main

### Code
- [onDeck/Utilities/MemoryDiagnostics.swift](../onDeck/Utilities/MemoryDiagnostics.swift) — probe harness
- [onDeck/Managers/GameMonitor.swift:198-233](../onDeck/Managers/GameMonitor.swift#L198-L233) — JSON pipeline per poll
- [onDeck/Networking/MLBStatsAPI.swift:81-84](../onDeck/Networking/MLBStatsAPI.swift#L81-L84) — `decodeLiveFeed` (stubbed in day 2 test)
- [onDeck/Notifications/NotificationManager.swift:87-91](../onDeck/Notifications/NotificationManager.swift#L87-L91) — `UNNotificationAttachment` construction
- [onDeck/Views/SettingsView.swift:115-126](../onDeck/Views/SettingsView.swift#L115-L126) — Settings onAppear/onDisappear + activation policy

### Prior investigation
- `docs/memory-investigation.md` — original "bounded plateau" hypothesis (now disproven)
- `docs/incremental-feed-parse.md` — designed but unimplemented fix for Problem 1

### Logs (this folder)
- [`logs/probe-overnight-2026-04-13.csv`](logs/probe-overnight-2026-04-13.csv) — first overnight, partial
- [`logs/probe-day1-2026-04-13.csv`](logs/probe-day1-2026-04-13.csv) — full day 1 ablation probe
- [`logs/probe-day2-2026-04-14.csv`](logs/probe-day2-2026-04-14.csv) — day 2 stub-decode test
- [`probe-runbook.md`](probe-runbook.md) — operational runbook used to conduct the probe

### CSV schema (v2)
| col | field | meaning |
|---|---|---|
| 1 | `timestamp` | ISO 8601 UTC |
| 2 | `event` | `tick`, `start`, `systemResume`, `popoutOpen`, `popoutClose`, `settingsOpen[+0.5s/+2s/+5s]`, `settingsClose[...]` |
| 3 | `footprint_mb` | `phys_footprint` via `TASK_VM_INFO` — **primary metric** |
| 4 | `rss_mb` | `resident_size` (includes shared framework pages) |
| 5 | `compressed_mb` | compressor-held pages |
| 6 | `vsize_mb` | virtual size |
| 7 | `malloc_inuse_mb` | bytes live in default malloc zone |
| 8 | `malloc_alloc_mb` | bytes reserved from OS for default malloc zone |
| 9 | `active_games` | games within 15 min of start time |
| 10 | `monitored_games` | size of `monitoredGames` dict |
| 11 | `latest_feeds` | size of `latestFeeds` dict |
| 12 | `cached_feed_bytes` | total bytes in `cachedFeedData` |
| 13 | `team_logos` | in-memory `TeamLogoCache` entry count |
| 14-15 | `notif_delivered` / `notif_pending` | UserNotifications counts |
| 16 | `poll_count` | completed poll cycles since launch |
| 17-18 | `popout_open` / `popout_total_s` | popout state |
| 19-23 | `f_*` | runtime flag states |
| 24-25 | `url_cache_mem` / `url_cache_disk` | URLCache sizes |

---

## Next step

Fix design, to be drafted as a separate doc after brainstorming.

Planned scope:
1. **Incremental-parse refactor** — replace JSON pipeline per `docs/incremental-feed-parse.md`. Eliminates ~36 MB/hr heap growth.
2. **Notifications** — drop image attachment, or switch to pre-decoded thumbnails. Eliminates ~7 MB/hr non-heap growth.
3. **Pressure relief** — call `malloc_zone_pressure_relief(nil, 0)` after game-end cleanup and on `systemResume`. Ensures allocator releases free pages without waiting for system pressure.
4. **Opportunistic cache eviction** — drop `URLCache.shared`, `TeamLogoCache.memory`, old `latestFeeds` entries when no games are active.
