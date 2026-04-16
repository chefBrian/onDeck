# On Deck — Memory Fix Design

**Branch**: `memory-probe-2` • **Status**: Plan of record. Phase 1 trace complete (92k ops across 2 slate days, v1.csv + v2 rotated CSV preserved). Patcher implementation is commit 1.

This doc decides the fix strategy for the three memory-growth mechanisms identified in [FINDINGS.md](FINDINGS.md). Decisions in sections A–E are the plan of record unless the user redirects. An implementation plan follows in a separate document.

Scope decisions made during brainstorming:
- Typed-patcher ships pure (no old-pipeline safety net). User will local-test before merging.
- Per-player notification headshots are retained. The ~7 MB/hr cost is accepted for now.
- Settings panel spike is deferred to a future diagnostics pass.
- Fix C (`malloc_zone_pressure_relief`) is deferred. Fix A is designed to make heap amplitude small enough that pressure relief becomes a no-op. If post-fix telemetry shows residual retention, Fix C ships in a follow-up round.

---

## A — JSON parsing (heap, ~36 MB/hr)

### Decision

Replace the four-step pipeline in [GameMonitor.swift:211-214](../onDeck/Managers/GameMonitor.swift#L211-L214) with a typed patcher that mutates `LiveFeedData` fields directly from RFC 6902 operations. Delete [JSONPatch.swift](../onDeck/Utilities/JSONPatch.swift) and the `cachedFeedData` / `cachedTimecodes` dicts on `GameMonitor`. The patcher uses a two-tier classifier: paths under a relevant prefix that don't match a registered handler trigger a reseed; paths outside any relevant prefix are logged-and-skipped (so MLB adding decorative fields doesn't churn the cache).

### End-state data flow

```
First poll per game:
  fetchLiveFeedRaw → Codable decode once → latestFeeds[gamePk] = LiveFeedData (mutable, includes timeStamp)

Subsequent polls (URL built from latestFeeds[gamePk].timeStamp):
  fetchDiffPatch → one of:
    .noChanges                     → no-op (2-byte bandwidth win preserved)
    .patches(ops)                  → apply-to-copy → commit → processFeed(...)
                                     unknown-under-relevant → reseed
                                     unknown-outside-relevant → log-and-skip
    .fullUpdate(d)                 → Codable decode once → latestFeeds[gamePk] = decoded
    network/transient error        → keep latestFeeds[gamePk]; null its timeStamp; full fetch next cycle
```

`cachedFeedData: [Int: Data]` and `cachedTimecodes: [Int: String]` are deleted entirely. `latestFeeds` becomes the single source of truth — including the diffPatch timecode, which lives at `LiveFeedData.timeStamp`.

### `LiveFeedData` changes

Currently a `let`-only struct ([MLBStatsAPI.swift:267-297](../onDeck/Networking/MLBStatsAPI.swift#L267-L297)). Three changes:

1. **All fields become `var`** so the patcher can mutate in place. Type stays `Sendable`.
2. **Add `var timeStamp: String?`** sourced from `/metaData/timeStamp`. This replaces the deleted `cachedTimecodes` dict — the timestamp semantically *is* feed state.
3. **Replace formatted-string `PlayerGameStats` with raw typed structs.** Today `PlayerGameStats` stores `battingLine: String?` / `pitchingLine: String?`. Field-level patches like `/.../stats/batting/hits → 3` can't update those formatted strings without the surrounding raw fields. New shape:

```swift
struct PlayerGameStats: Sendable, Equatable {
    var batting: PlayerBattingStats?
    var pitching: PlayerPitchingStats?
}

struct PlayerBattingStats: Sendable, Equatable {
    var atBats: Int?; var hits: Int?; var runs: Int?
    var doubles: Int?; var triples: Int?; var homeRuns: Int?
    var rbi: Int?; var baseOnBalls: Int?; var strikeOuts: Int?; var stolenBases: Int?

    var formatted: String? { /* moved from MLBStatsAPI.formatBattingLine */ }
}

struct PlayerPitchingStats: Sendable, Equatable {
    var inningsPitched: String?; var hits: Int?
    var earnedRuns: Int?; var strikeOuts: Int?
    var baseOnBalls: Int?; var numberOfPitches: Int?

    var formatted: String? { /* moved from MLBStatsAPI.formatPitchingLine */ }
}
```

Raw is the source of truth; formatted strings are derived view data. View call sites change from `stats.battingLine` to `stats.batting?.formatted`. Memory delta over a 15-game slate is ~3 KB (negligible). Field-level patches become one-line mutations with no rebuild step.

4. **`LiveFeedData: Sendable, Equatable`** — synthesized `Equatable` enables the test strategy (compare patched vs Codable-decoded reference) without hand-written `==`.

### RFC 6902 paths the patcher must cover

The authoritative path list comes from **Phase 1 trace data** (see "Logging strategy" below) — captured from real MLB diffPatch traffic over at least one full slate. The table below is the expected shape derived from [`parseLiveFeedResponse`](../onDeck/Networking/MLBStatsAPI.swift#L139-L177); it is a starting hypothesis, not the final list. Cross-reference with the Phase 1 CSV before writing handlers.

| Path prefix | Target field(s) | Value shape |
|---|---|---|
| `/metaData/timeStamp` | `feed.timeStamp` | string |
| `/gameData/status/abstractGameState` | `gameState` | string |
| `/gameData/status/detailedState` | `detailedState` | string |
| `/gameData/teams/home/name` | `homeTeam` | string |
| `/gameData/teams/home/id` | `homeTeamID` | int |
| `/gameData/teams/away/name` | `awayTeam` | string |
| `/gameData/teams/away/id` | `awayTeamID` | int |
| `/liveData/plays/currentPlay/matchup/batter/id` | `currentBatterID` | int |
| `/liveData/plays/currentPlay/matchup/batter/fullName` | `currentBatterName` | string |
| `/liveData/plays/currentPlay/matchup/pitcher/id` | `currentPitcherID` | int |
| `/liveData/plays/currentPlay/matchup/pitcher/fullName` | `currentPitcherName` | string |
| `/liveData/plays/currentPlay/about/isComplete` | `isPlayComplete` | bool |
| `/liveData/plays/currentPlay/result/event` | `lastPlayEvent` | string |
| `/liveData/plays/currentPlay/result/description` | `lastPlayDescription` | string |
| `/liveData/plays/currentPlay/count/balls` | `balls` | int |
| `/liveData/plays/currentPlay/count/strikes` | `strikes` | int |
| `/liveData/plays/currentPlay/count/outs` | `outs` | int |
| `/liveData/linescore/currentInning` | `inning` | int |
| `/liveData/linescore/inningHalf` | `inningHalf` | string |
| `/liveData/linescore/inningState` | `inningState` | string |
| `/liveData/linescore/teams/home/runs` | `homeScore` | int |
| `/liveData/linescore/teams/away/runs` | `awayScore` | int |
| `/liveData/linescore/offense/first/id` | `runnerOnFirst` | int or null |
| `/liveData/linescore/offense/second/id` | `runnerOnSecond` | int or null |
| `/liveData/linescore/offense/third/id` | `runnerOnThird` | int or null |
| `/liveData/boxscore/teams/home/battingOrder` | `homeBattingOrder` | `[int]` (replace) |
| `/liveData/boxscore/teams/home/battingOrder/<n>` | `homeBattingOrder[n]` | int |
| `/liveData/boxscore/teams/away/battingOrder` | `awayBattingOrder` | `[int]` |
| `/liveData/boxscore/teams/home/pitchers` | `homePitchers` | `[int]` |
| `/liveData/boxscore/teams/home/pitchers/-` | `homePitchers.append` | int |
| `/liveData/boxscore/teams/away/pitchers` | `awayPitchers` | `[int]` |
| `/liveData/boxscore/teams/<side>/players/ID<n>` | `playerStats[n]` | player object (decode `.stats.batting`/`.stats.pitching` into typed structs) |
| `/liveData/boxscore/teams/<side>/players/ID<n>/stats/batting` | `playerStats[n].batting` | full `PlayerBattingStats` decode |
| `/liveData/boxscore/teams/<side>/players/ID<n>/stats/batting/<field>` | `playerStats[n].batting.<field>` | int (direct write) |
| `/liveData/boxscore/teams/<side>/players/ID<n>/stats/pitching` | `playerStats[n].pitching` | full `PlayerPitchingStats` decode |
| `/liveData/boxscore/teams/<side>/players/ID<n>/stats/pitching/<field>` | `playerStats[n].pitching.<field>` | string or int (direct write) |

### Patcher structure

**Two-tier dispatch** per op:

1. **Registered handler** (leaf or subtree) → apply typed mutation.
2. **Unregistered path** → `UnknownPatchLogger` record, skip silently.

The earlier plan's "throw on unknown-under-relevant-prefix" middle tier was removed after Phase 1 trace analysis: `/liveData/plays/currentPlay` contains hundreds of decorative leaves (hotColdZones, pitchIndex, playEvents, etc.) that would force reseed on every cycle. Prefix-based dispatch is too broad at this level of the tree.

The trade-off: a field we should have registered but didn't will be silently skipped, causing stale UI for that field rather than a reseed. Caught in local testing and via `UnknownPatchLogger` review. Far better than reseed-storms.

**RFC 6902 ops handled:** `add`, `remove`, `replace`, `copy`, `move`. The current `JSONPatch.swift` silently drops `copy`/`move`; the typed patcher handles them explicitly for paths we model.

**Subtree cases (parent-level replaces):**

Phase 1 trace (92k rows across 2 slate days) showed only leaf-level `replace` ops — MLB did not emit parent-level replaces for `/currentPlay/count`, `/matchup/batter`, `/matchup/pitcher`, `/linescore/offense/*`, or stats subtrees during observation. Register **defensively** for the cases most likely to appear in future (full player object on `add`, full stats subtree on initial player entry):

- `add /liveData/boxscore/teams/<side>/players/ID<n>` (new player enters game — decode full player object including stats)
- `replace /liveData/boxscore/teams/<side>/players/ID<n>/stats/batting` (defensive — full batting line)
- `replace /liveData/boxscore/teams/<side>/players/ID<n>/stats/pitching` (defensive — full pitching line)

If MLB ever replaces a parent we haven't registered, the unknown op logs via `UnknownPatchLogger` and the UI fields stay stale for one cycle until a following scalar `replace` corrects them. Acceptable.

**Move/copy cases (typed handlers, not log-and-skip):**

From the trace, the only `copy`/`move` destinations that touch modeled state *and* use a meaningful (non-zero-init) `from` are the three runner-advance transitions on `/liveData/linescore/offense/*`:

```
move /offense/second <- /offense/first   (1→2)
move /offense/third  <- /offense/first   (1→3, double with runner scoring from first, etc.)
move /offense/third  <- /offense/second  (2→3)
```

Register typed handlers for these three cases only:

```swift
case ("move", "/liveData/linescore/offense/second"):
    guard op["from"] as? String == "/liveData/linescore/offense/first" else {
        UnknownPatchLogger.shared.record(op: opType, path: path, from: op["from"] as? String, value: nil)
        break
    }
    feed.runnerOnSecond = feed.runnerOnFirst
    feed.runnerOnFirst = nil
// (and similar for /third ← /first or /second)
```

**Everything else — `copy` on `/offense/*`, any `copy`/`move` on unmodeled paths — falls through to `UnknownPatchLogger` + skip.** Including:
- `copy /offense/<base> <- /allPlays/<n>/matchup/batter` (batter reaches base). The `from` path is unmodeled; we skip and rely on the accompanying scalar `replace /offense/<base>/id` ops to set runner IDs.
- Zero-init copies with `from = /currentPlay/result/rbi` blasted across player stat fields when a player enters. Observable-safe to skip (formatters return `nil` when `atBats == nil || atBats == 0` without activity, identical to formatters when stats are explicitly 0).
- Cross-type moves on `/boxscore/topPerformers/*` (destination unmodeled).

**Implementation sketch:**

```swift
struct LiveFeedPatcher {
    /// Apply patches to a copy. Caller commits on success — partial mutation never escapes on throw.
    static func apply(_ ops: [[String: Any]], to feed: inout LiveFeedData) throws {
        var working = feed
        for op in ops {
            guard let opType = op["op"] as? String,
                  let path = op["path"] as? String else { continue }
            applyOne(opType: opType, path: path, value: op["value"], from: op["from"] as? String, feed: &working)
        }
        feed = working
    }

    private static func applyOne(opType: String, path: String, value: Any?, from: String?, feed: inout LiveFeedData) {
        switch (opType, path) {
        case ("replace", "/metaData/timeStamp"):
            feed.timeStamp = value as? String
        case ("replace", "/liveData/plays/currentPlay/count/balls"):
            feed.balls = intOr(value, default: feed.balls)
        // ... one case per leaf path from the table above
        case ("add", let p) where p.hasPrefix("/liveData/boxscore/teams/home/players/ID"):
            applyHomePlayerPatch(opType: opType, path: p, value: value, feed: &feed)
        case ("move", "/liveData/linescore/offense/second"),
             ("move", "/liveData/linescore/offense/third"):
            applyOffenseMove(path: path, from: from, feed: &feed)
        // ... other subtree and copy/move handlers
        default:
            UnknownPatchLogger.shared.record(op: opType, path: path, from: from, value: value)
        }
    }
}
```

Note: no `PatcherError.unknownPath`, no `throws` on `apply`. Under two-tier dispatch there's nothing to throw — unknown paths are recorded and skipped inside the switch. `apply` still operates on a working copy for future-proofing, but the copy is only needed if we later reintroduce throwing paths.

### Call-site changes in `GameMonitor.pollSingleGame`

- `cachedFeedData` and `cachedTimecodes` removed from the class
- Seed marker is `latestFeeds[gamePk] != nil`; diffPatch URL is built from `latestFeeds[gamePk]?.timeStamp`
- `.patches` branch uses local-var + assign-back (never `&latestFeeds[gamePk]!`):
  ```swift
  guard var feed = latestFeeds[gamePk] else { /* reseed path */ return }
  LiveFeedPatcher.apply(ops, to: &feed)
  latestFeeds[gamePk] = feed
  ```
- `.fullUpdate` branch decodes via Codable and assigns `latestFeeds[gamePk]`
- No-seed branch unchanged (full fetch + Codable)
- **Network/transient error** (outer catch): preserve `latestFeeds[gamePk]` so UI doesn't flicker; null its `timeStamp` only, forcing a full fetch next cycle

There is no `PatcherError.unknownPath` under two-tier dispatch — unknown paths are recorded and silently skipped. The local-var pattern is retained for the same observability / Observable semantics benefits described earlier.

### Call-site changes in `MLBStatsAPI`

Largely unchanged. `fetchDiffPatch` still returns `DiffPatchResult` with `.patches([[String: Any]])`. The individual patch dicts are small (typically tens of bytes each), and `JSONSerialization.jsonObject` on the ~200B diffPatch response is a rounding error compared to the 500KB feed parse we're eliminating.

### Logging strategy

Two distinct loggers, different lifetimes:

**Phase 1 — `DiffPatchTraceLogger` (DEBUG-only, throwaway).** Already shipped on this branch. Records every `(timestamp, gamePk, op, path, from, value_preview)` tuple from `fetchDiffPatch` to `~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-diffpatch-trace.csv`. The `from` column captures the source path for `copy` and `move` ops (empty for other ops). Run for one full slate to derive the empirical handler list. **Removed once the patcher is written.**

Quick analysis command:
```bash
tail -n +2 ~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-diffpatch-trace.csv \
  | awk -F',' '{print $3, $4}' | sort | uniq -c | sort -rn
```

**Phase 2 — `UnknownPatchLogger` (all builds, permanent).** Single category under two-tier dispatch: any op the patcher doesn't have a handler for. Writes to `onDeck-unknown-patches.csv`, columns `timestamp, gamePk, op, path, from, value_preview`. Prefixed in Console as `[LiveFeedPatcher] unknown: …` for real-time visibility. Same 10 MB rotation policy. Reviewed periodically; any frequently-seen path on a field we care about becomes a new handler in a follow-up.

### Trade-offs

- **Maintenance burden**: the patcher becomes the canonical shape definition for live feed state. If MLB adds a new field we want to consume, we update both `LiveFeedData` and the patcher. That's one extra edit per new field, acceptable.
- **Unknown-path silent skip**: fields we forgot to register become UI staleness, not reseeds. Caught in local testing and via `UnknownPatchLogger` CSV review. The trade-off vs. reseed-on-unknown: we chose skip because `/liveData/plays/currentPlay` is a decorative-field magnet (~500+ leaves per slate: hotColdZones, pitchIndex, playEvents, etc.); reseed-on-unknown-under-that-prefix would fire every cycle.
- **Zero-init copy pattern**: Phase 1 revealed MLB uses `copy` ops as a compression trick to bulk-initialize new players' stat fields from a known-zero source (e.g. `copy /stats/batting/hits <- /currentPlay/result/rbi` ×40 fields when a player enters). Safe to skip because our formatters (`formatBattingLine` / `formatPitchingLine`) return `nil` when stats are `nil` OR when stats are explicitly `0` with no activity — observable output is identical either way. Verified across 92k trace rows / 2 slate days.
- **Initial seed still pays Codable cost**: once per game per day (or after `.fullUpdate`). Amortized heap cost is negligible — 30 games × 1 full decode ≠ meaningful vs. the old 30 games × 6/min × full decode.
- **diffPatch "no changes" path untouched**: 2-byte bandwidth win preserved.

### Expected heap impact

FINDINGS measured 26 MB/hr of heap growth attributable to the Codable decode step (stub-decode probe). Fix A also eliminates the `JSONSerialization` + `JSONPatch.apply` + reserialize steps surrounding it (the remaining ~10 MB/hr of heap growth observed in the same window). We expect Fix A to eliminate most or all of the ~36 MB/hr, but the second-order attribution is inferential — the existing memory probe will confirm the actual impact post-fix.

---

## B — Notifications (non-heap, ~7 MB/hr)

### Decision

No change. Per-player headshot attachments retained. The ~7 MB/hr cost is accepted.

### Rationale

User values the visual recognition of per-player headshots in Notification Center. A single shared icon was ruled out as a half-measure that loses the feature's value. Apple's public documentation does not describe the in-process retention we observed, so any tactical fix (pre-decoded thumbnails, bundle-resource vs cache-dir tricks) is speculative without further probing.

### Future work

Filed in the [deferred diagnostics](#deferred-diagnostics) section below.

---

## C — Malloc retention (~200 MB held overnight)

### Decision

**Deferred.** Fix A addresses the allocator-pressure root cause; Fix C addresses a symptom Fix A is designed to prevent. Shipping both together would bundle a measured fix with an unmeasured one, making post-fix attribution muddy.

Plan: ship Fix A, observe overnight footprint via the existing memory probe CSV. If `malloc_alloc_mb` still climbs to >80 MB and stays there overnight, open a follow-up round for `malloc_zone_pressure_relief` at idle transitions (last-game-end, system-wake, day-rollover). Until then, the call sites and helper described in earlier drafts are not implemented.

Belt-and-suspenders argument for shipping it anyway is weak when the belt (Fix A) is specifically designed to make the suspenders unnecessary.

---

## D — Opportunistic cache eviction

### Decision

Minimal. Only one change:

- **Day rollover**: `latestFeeds` is already cleared by `GameMonitor.stopMonitoring()` at day rollover (see [GameMonitor.swift:93](../onDeck/Managers/GameMonitor.swift#L93)). No change needed. Verify the rollover path actually calls full `stopMonitoring()`, not `stopMonitoring(gamePk:)`.

### Not changing

- **`URLCache.shared`** — FINDINGS shows it was 30–450 KB throughout the probe, not a contributor. Leaving alone.
- **`TeamLogoCache.shared.memory`** — 30 team logos max, fixed upper bound. Not a contributor.
- **`latestFeeds` during the day** — intentionally retained for finished games so the Done section can render boxscore stats. Per-entry cost is small (one `LiveFeedData` struct + string tables for ~50 players). A 15-game slate holds maybe 100 KB here. Not worth touching.

### Rationale

No evidence any of these caches contribute to end-of-day growth. Optimizing them without evidence is churn. Revisit if post-fix telemetry surfaces something.

---

## E — Ship order, risk, testing

### Order

0. **Phase 1 trace logger** — already shipped on this branch ([DiffPatchTraceLogger.swift](../onDeck/Utilities/DiffPatchTraceLogger.swift)). Run through one full slate to capture real `(op, path)` data.
1. **Commit 1** — add `LiveFeedPatcher`, `UnknownPatchLogger`, reshape `LiveFeedData` (typed raw stats, `timeStamp` field, `Equatable`), unit tests. No call-site changes yet. Tests green.
2. **Commit 2** — wire patcher into `GameMonitor.pollSingleGame` using the local-var pattern; delete `cachedFeedData`, `cachedTimecodes`, `JSONPatch.swift`, and the Phase 1 `DiffPatchTraceLogger`. Phase 2 `UnknownPatchLogger` stays.

Fix C is deferred (see section C). Fix D is verification-only.

### Risk per fix

| Fix | Risk | Mitigation |
|---|---|---|
| A | Phase 1 trace misses an edge-case path (rain delay, replay review, doubleheader transition) | Two-tier classifier — uncovered paths under relevant prefixes reseed (correct fallback); outside-relevant paths log-and-skip. `UnknownPatchLogger` flags both for ongoing review |
| A | `LiveFeedData` field mutation under Swift 6 strict concurrency breaks `Sendable` | Struct value semantics preserve `Sendable`; `var` fields are fine as long as the type is `Sendable` and the patcher is `MainActor` (matches `GameMonitor`) |
| A | Apply-to-copy adds a struct copy per patch batch | Copy is COW, effectively free until mutation. Trade-off: throws never leak partial state to UI |
| A | Network error mid-slate clears state, UI flickers | Mitigated: outer catch preserves `latestFeeds[gamePk]`, only nulls `timeStamp`. UI shows last-known data; next cycle does full fetch |

### Testing strategy

**Fix A verification**:
- **Unit tests**: canned full-feed JSON + canned diff-patch sequences. For each test case, assert patched `LiveFeedData == LiveFeedData` produced by a full Codable decode of the equivalent final JSON. (`Equatable` synthesis makes this one-line.) Covers every path from the Phase 1 trace.
- **Runtime watch**: run branch locally for ≥3 slate days; check `onDeck-unknown-patches.csv` after each slate. `under-relevant` entries → investigate, add handler if needed. `outside-relevant` entries → triage occasionally; promote any that turn out to matter.

**End-to-end verification**:
- Run branch locally for a few slate days before merging to main. The existing memory probe CSV (re-enabled if needed) logs footprint. Expected outcome: overnight idle baseline drops from ~200 MB to <80 MB; peak during active slate drops from ~260 MB to ~100–120 MB. If overnight baseline stays >80 MB, Fix C becomes a follow-up round.

### Commit boundaries

Already landed:
- Phase 1 `DiffPatchTraceLogger` (DEBUG-only) + `fetchDiffPatch` call site. Throwaway — removed in commit 2.

Planned:
1. **Commit 1** — `LiveFeedPatcher` + `UnknownPatchLogger` + reshaped `LiveFeedData` (typed raw stats with `formatted` computed properties, `var` fields, `timeStamp`, `Equatable`) + unit tests. No call-site changes. Tests green.
2. **Commit 2** — wire patcher into `GameMonitor.pollSingleGame` with local-var pattern; delete `cachedFeedData`, `cachedTimecodes`, `JSONPatch.swift`, `DiffPatchTraceLogger.swift`; update view call sites from `stats.battingLine` to `stats.batting?.formatted`.

Each is independently revertable. Fix C (deferred) would be a separate commit in a follow-up round if telemetry warrants.

---

## Deferred diagnostics

Captured here so nothing slips. None of these are in scope for this fix round.

- **Notification attachment retention probe**. Before deciding whether to drop headshots in a future round, run a targeted test: send N notifications with distinct headshots, purge them all, observe `phys_footprint` and `malloc_alloc_mb` before and after. Likely easiest to wire as a debug menu item that fires 50 test notifications and purges them on a timer. Goal: distinguish "purges work but peak ratchets malloc" from "purges don't release attachment retention".
- **Settings panel spike**. FINDINGS documents a +230 MB transient on every Settings open/close, releasing within 2s. Not a steady-state contributor. Would require Instruments (Allocations) to pin the responsible class. Low priority, cosmetic only.

---

## Summary of decisions

| Problem | Decision | Expected impact |
|---|---|---|
| A — JSON parsing | Typed `LiveFeedPatcher`, two-tier dispatch (registered handler → apply; else `UnknownPatchLogger` + skip). Handles add/remove/replace/copy/move. Raw typed player stats + computed `formatted`. `timeStamp` moves into `LiveFeedData`. Local-var pattern. Delete `JSONPatch.swift`, `cachedFeedData`, `cachedTimecodes` | Most or all of ~36 MB/hr heap eliminated (verify post-fix) |
| B — Notifications | No change, accept cost | none (deferred) |
| C — Malloc retention | **Deferred.** Ship Fix A, measure overnight footprint, revisit only if `malloc_alloc_mb` still climbs | none (this round) |
| D — Cache eviction | No changes; existing day-rollover `stopMonitoring` already correct | none |
| E — Ship order | Phase 1 trace logger (done) → commit 1 (patcher + types + tests) → commit 2 (wire-in + deletions) | End-of-day footprint: ~260 MB → ~100–120 MB projected; overnight idle ~200 MB → <80 MB |
