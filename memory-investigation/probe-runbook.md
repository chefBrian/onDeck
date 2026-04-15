# Memory Probe Runbook — 2026-04-13

Walkthrough for tomorrow's memory investigation. Branch: `memory-probe-2`.

The goal is to identify what's causing RSS to climb from ~50 MB at launch to ~263 MB after a full day of games. Previous investigation (`docs/memory-investigation.md`) concluded a ~190 MB plateau — that was wrong. Growth continues.

The harness on this branch adds:
- CSV logger writing one row per minute to `~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv` (the app is sandboxed, so `Caches` resolves to the container path — not `~/Library/Caches`)
- Five runtime flags for bisection ablations (no rebuild needed between flips)
- Sleep/wake + popout open/close event rows

**Bundle-id note**: debug builds use `dev.bjc.onDeck.debug`; release builds use `dev.bjc.onDeck`. Every `defaults` command below assumes you're running the debug build from `build/Build/Products/Debug/`. If you run the release build instead, drop the `.debug` suffix on every `defaults` command.

---

## Prep (morning, before first game)

### 1. Confirm you're on the right branch and build

```bash
git -C "/Users/brian/Dev Me/onDeck" status
# expect: On branch memory-probe-2, clean
```

Build the debug configuration. The harness adds prints but no runtime cost unless `memoryDiagnostics` is set.

```bash
xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build
```

Launch from the build output **from a terminal** so we can capture stderr — GUI launches swallow `print()`:

```bash
./build/Build/Products/Debug/onDeck.app/Contents/MacOS/onDeck > /tmp/ondeck.log 2>&1 &
```

(If running the Release copy in `/Applications`, it still works but `print` goes to `/dev/null`.)

### 2. Enable diagnostics logging

```bash
defaults write dev.bjc.onDeck.debug memoryDiagnostics -bool YES
```

Quit and relaunch the app. On next boot you should see (in `/tmp/ondeck.log`):

```
[MemoryProbe] Logging to /Users/brian/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv
```

Confirm the file gets a header row and then starts filling:

```bash
CSV=~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv
head -3 "$CSV"
```

Keep `$CSV` exported for the rest of the session so every tail/awk command below just works.

### 3. Get a second terminal ready for flag flips

We'll flip flags with `defaults write` while the app runs. Each flip shows up in the next CSV row (within 60s). Have these commands ready:

```bash
# enable a flag (debug build domain)
defaults write dev.bjc.onDeck.debug memDiagNoPopout             -bool YES
defaults write dev.bjc.onDeck.debug memDiagNoNamespace           -bool YES
defaults write dev.bjc.onDeck.debug memDiagSkipNotifications     -bool YES
defaults write dev.bjc.onDeck.debug memDiagStubFeedDecode        -bool YES
defaults write dev.bjc.onDeck.debug memDiagResetURLCache         -bool YES
defaults write dev.bjc.onDeck.debug memDiagNoActivationPolicy    -bool YES

# disable
defaults delete dev.bjc.onDeck.debug memDiagNoPopout
# (etc.)
```

**Important**: `memDiagNoNamespace` is read during SwiftUI body evaluation. If you flip it mid-run, close and reopen the popout so the view tree rebuilds cleanly.

**`memDiagStubFeedDecode` breaks the live UI on purpose** — no scores/batters/pitchers will update. Only enable for the final ablation window.

---

## Pre-flight: isolate the Settings-open spike

Before the main schedule, knock out the Settings activation-policy question so it doesn't add noise to flag windows. Takes ~5 minutes.

The [`.onAppear`/`.onDisappear`](../onDeck/Views/SettingsView.swift) on `SettingsView` toggles `NSApplication.activationPolicy` between `.accessory` and `.regular`. That's been shown to cause a transient spike on open. `memDiagNoActivationPolicy` short-circuits the toggle so we can A/B it.

Procedure:

```bash
CSV=~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv

# A: activation policy ON (default). Open and close Settings 3x.
#    Wait ~10s between each open/close so the trailing snapshots land.
# ...open/close Settings 3 times via the menu bar...

# B: activation policy OFF
defaults write dev.bjc.onDeck.debug memDiagNoActivationPolicy -bool YES
# ...open/close Settings 3 more times...

# then disable
defaults delete dev.bjc.onDeck.debug memDiagNoActivationPolicy
```

Analyze:

```bash
grep -E "settings(Open|Close)" "$CSV" | \
  awk -F, '{printf "%s %-22s footprint=%6s malloc_alloc=%6s\n", $1, $2, $3, $8}'
```

What to expect:
- Spike magnitude should be visibly smaller (or absent) under flag B vs flag A
- If same spike in both → it's something else in `onAppear` (the Form construction, Scene retention) and activation policy is off the hook
- Each `settingsClose+5s` footprint should be back near the pre-open baseline. If it drifts up over the 6 opens, that's the per-open leak signal

## Schedule for the day

The CSV captures everything; these are just the windows we want distinct slopes for. Note the Pacific time when each flag flips (or just rely on the timestamps in the CSV).

| Window | Flags set | What it tests |
|---|---|---|
| 11:00 – 12:30 | _(none)_ | **Idle baseline** — no games yet. Is there any growth pre-slate? |
| 12:30 – 14:00 | _(none)_ | **Live baseline** — first games of the slate, all defaults |
| 14:00 – 15:30 | `memDiagNoPopout=YES` (also close popout) | Isolates SwiftUI / popout retention |
| 15:30 – 17:00 | `memDiagNoPopout=NO`, `memDiagNoNamespace=YES`, reopen popout | Isolates matchedGeometryEffect / @Namespace |
| 17:00 – 18:30 | `memDiagNoNamespace=NO`, `memDiagSkipNotifications=YES` | Isolates UNNotifications |
| 18:30 – 20:00 | `memDiagSkipNotifications=NO`, `memDiagResetURLCache=YES` | Isolates URLSession / URLCache |
| 20:00 – end  | `memDiagResetURLCache=NO`, `memDiagStubFeedDecode=YES` | Isolates Codable / parse allocator. UI goes blank — expected |

At each flag flip:
```bash
# flip the flag
defaults write dev.bjc.onDeck.debug <flag> -bool YES  # or NO / delete

# confirm it landed
defaults read dev.bjc.onDeck.debug | grep memDiag
```

No app relaunch needed for any flag except if you want a truly clean SwiftUI state for the namespace flag — in that case quit and relaunch the popout.

---

## What to watch for

### During the day

Tail the CSV to eyeball progress:

```bash
CSV=~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv
tail -f "$CSV" | awk -F, '{print $1, $2, "footprint=" $3 "MB", "malloc=" $7 "MB", "polls=" $16}'
```

**Primary metric is `footprint_mb` (column 3)** — it matches Activity Monitor's "Memory" column (phys_footprint). `rss_mb` is resident size which overcounts by ~110 MB (shared framework pages). If footprint at the start of a flag window is X and at the end is Y, that window's slope is `(Y-X) / hours`. We care about **slope change**, not absolute.

### End of day — analyze

```bash
# copy off a snapshot so you don't worry about the file being rewritten
CSV=~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv
cp "$CSV" /tmp/memlog-$(date +%Y%m%d).csv
```

For each flag window, compute:

- **RSS slope** (MB/hour)
- **RSS slope normalized to active game count** (MB/hour/active_game)
- **malloc_inuse slope** — attributes growth to heap vs other (IOSurface, CGImage, etc.)
- **delta in tracked dicts** (`cachedFeedData`, `latestFeeds`, `teamLogos`, `notifDelivered`, `notifPending`) — are any of our own caches growing?

A quick way to dump per-window stats:

```bash
# substitute time ranges to match your flag windows
awk -F, 'NR==1 || ($1 >= "2026-04-13T14:00" && $1 < "2026-04-13T15:30")' /tmp/memlog-*.csv
```

### Decision tree

- **Slope flattens with `memDiagNoPopout`** → SwiftUI view-tree retention. Deeper dive: Instruments Allocations with popout-on vs popout-off snapshots.
- **Slope flattens with `memDiagNoNamespace`** → matchedGeometryEffect retention. Consider removing animations or scoping the namespace more tightly.
- **Slope flattens with `memDiagSkipNotifications`** → something in UNNotification flow. Check `notif_delivered` / `notif_pending` curves and VM Tracker for image memory.
- **Slope flattens with `memDiagResetURLCache`** → URLSession / connection pool / TLS state. Consider a dedicated URLSession we can reset.
- **Slope flattens with `memDiagStubFeedDecode`** → Foundation JSONDecoder / JSONSerialization. Prior investigation's "plateau" was wrong — time to revisit `docs/incremental-feed-parse.md`.
- **Pre-flight showed activation-policy off flattens Settings spike** → fix is to drop or gate the `setActivationPolicy` toggle in `SettingsView.swift`. If footprint also drifted up across 6 opens, there's an additional per-open leak to chase separately.
- **No flag flattens slope** → framework-internal (SwiftUI graph, Observation, URLSession internals). Fallback: Instruments Allocations + Leaks + VM Tracker run (see below).

### Fallback: Instruments

If CSV bisection doesn't nail it, run Instruments mid-day during peak (5+ active games):

1. Xcode → Open Developer Tool → Instruments → **Allocations** template
2. Attach to the running onDeck process
3. Record 20–30 minutes
4. Add two additional instruments to the same document: **Leaks** and **VM Tracker**
5. Take a **Mark Generation** snapshot every 5 minutes during recording
6. After stop: 
   - Allocations → Statistics → sort by **Persistent Bytes** — look at top 20 call trees
   - Leaks → any entries = strong reference cycles, fix these first
   - VM Tracker → totals per category (IOSurface, CoreAnimation, Image IO, WebKit) — is non-heap memory growing?
   - Generations view — what persisted between each 5-min mark

---

## CSV column reference

| col | meaning |
|---|---|
| `timestamp` | ISO 8601 UTC |
| `event` | `tick`, `start`, `systemResume`, `popoutOpen`, `popoutClose` |
| `footprint_mb` | **primary metric** — phys_footprint, matches Activity Monitor |
| `rss_mb` | resident set size (includes ~110 MB of shared framework pages) |
| `compressed_mb` | bytes the memory compressor has swapped out |
| `vsize_mb` | virtual size |
| `malloc_inuse_mb` | default-zone bytes currently in use (heap) |
| `malloc_alloc_mb` | default-zone bytes allocated (reserved from OS) |
| `active_games` | games within 15 min of start (actively polling) |
| `monitored_games` | games in `monitoredGames` dict |
| `latest_feeds` | count of entries in `latestFeeds` (finished + live) |
| `cached_feed_bytes` | total bytes of raw JSON in `cachedFeedData` |
| `team_logos` | count of in-memory team logo images |
| `notif_delivered` | count delivered to Notification Center |
| `notif_pending` | count of pending (scheduled) notifications |
| `poll_count` | completed poll cycles since launch |
| `popout_open` | 1/0 |
| `popout_total_s` | cumulative seconds popout has been open |
| `f_noPopout` / `f_noNamespace` / `f_skipNotif` / `f_stubDecode` / `f_resetURLCache` | flag state |
| _(not in CSV)_ `memDiagNoActivationPolicy` | read by `SettingsView.onAppear` only; effect visible via `settingsOpen`/`settingsClose` event rows |
| `url_cache_mem` | `URLCache.shared.currentMemoryUsage` |
| `url_cache_disk` | `URLCache.shared.currentDiskUsage` |

---

## Tear-down

When the investigation is done:

```bash
# disable the logger
defaults delete dev.bjc.onDeck.debug memoryDiagnostics

# clean up any leftover flags
for k in memDiagNoPopout memDiagNoNamespace memDiagSkipNotifications memDiagStubFeedDecode memDiagResetURLCache; do
  defaults delete dev.bjc.onDeck.debug $k 2>/dev/null
done

# archive the log
mv ~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-memlog-v2.csv ~/Desktop/memlog-$(date +%Y%m%d).csv
```

Don't merge `memory-probe-2` back to main — this branch is disposable. Once we know the cause, the fix goes to a fresh branch off main. The harness itself is unused in production and should stay here for future probes.

---

## Appendix: what's in the harness

Files changed on this branch vs `main`:

- `onDeck/Utilities/MemoryDiagnostics.swift` _(new)_ — `MemoryDiagnostics.snapshot()` via Mach + malloc stats, `MemoryProbeLogger` CSV writer, `MemDiagFlags` enum
- `onDeck/Managers/GameMonitor.swift` — `pollCount` counter + `memoryDiagnosticsReport()` method
- `onDeck/Views/MenuBarView.swift` — `matchedGeometryConditional` helper, `TeamLogoCache.memoryCount` accessor, `FloatingPanel` gate + open/close telemetry
- `onDeck/Notifications/NotificationManager.swift` — `send()` early-return when `memDiagSkipNotifications`
- `onDeck/Networking/MLBStatsAPI.swift` — `decodeLiveFeed` stub when `memDiagStubFeedDecode`, `stubFeed` static
- `onDeck/App/AppState.swift` — `MemoryProbeLogger.start(appState:)` in init, `systemResume` event log, popout auto-open respects `memDiagNoPopout`
