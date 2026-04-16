# Phase 1 DiffPatch Trace — Findings

**Branch**: `memory-probe-2` • **Period**: 2026-04-14 through 2026-04-16 • **Status**: Complete

What MLB actually emits on `/feed/live/diffPatch`, observed by instrumenting `MLBStatsAPI.fetchDiffPatch` to CSV-log every RFC 6902 op returned. Evidence base for the patcher design in [FIX-DESIGN.md](FIX-DESIGN.md).

---

## Method

A DEBUG-only [`DiffPatchTraceLogger`](../onDeck/Utilities/DiffPatchTraceLogger.swift) writes one CSV row per RFC 6902 op returned from the API:

```
timestamp, gamePk, op, path, from, value_preview
```

- `from` column was added mid-investigation (v2) after v1 analysis revealed `copy`/`move` ops needed source-path visibility
- Captured cumulative ~120k rows across two slate days
- Files preserved: `onDeck-diffpatch-trace.v1.csv` (v1 schema), `onDeck-diffpatch-trace.csv.1` (rotated v2 peak)

## Sample sizes

| Capture | Rows | Slate days | Schema |
|---|---|---|---|
| v1 overnight 2026-04-14 → 2026-04-15 | 26,851 | 1 (partial) | no `from` column |
| v2 rotated 2026-04-15 18:53 → 2026-04-16 00:30 | ~65,000 | peak evening | includes `from` |
| v2 current 2026-04-16 00:30+ | tail | wind-down | includes `from` |

## Op distribution (v2 combined)

| op | count | % |
|---|---|---|
| `replace` | 99,062 | 75% |
| `remove` | 12,973 | 10% |
| `add` | 11,277 | 9% |
| `copy` | 7,943 | 6% |
| `move` | 893 | <1% |

All five RFC 6902 ops observed. The existing [`JSONPatch.swift`](../onDeck/Utilities/JSONPatch.swift) handles `replace`/`add`/`remove` and silently drops `copy`/`move` — so the old pipeline was already dropping ~7% of ops. We hadn't noticed because most of those ops touched decorative paths.

---

## Path universe

**4,825 unique paths** across a single slate. Broad categories:

| Category | Path shape | Our model touches? |
|---|---|---|
| Core scalars | `/currentPlay/count/{balls,strikes,outs}`, `/matchup/{batter,pitcher}/{id,fullName}`, `/linescore/{currentInning,inningHalf,inningState}`, `/linescore/teams/<side>/runs` | yes |
| Runner state | `/linescore/offense/{first,second,third}/{id,fullName,link}` | yes (id only) |
| Result | `/currentPlay/result/{event,description,eventType}` | partial |
| Player stats (fields we model) | `/boxscore/teams/<side>/players/ID<n>/stats/{batting,pitching}/<field>` | yes for ~16 fields |
| Player stats (decorative) | `/boxscore/teams/<side>/players/ID<n>/stats/{batting,pitching}/<field>` | no for ~40 fields (airOuts, flyOuts, pickoffs, gamesPlayed, etc.) |
| `hotColdZones` | `/currentPlay/matchup/batterHotColdZoneStats/stats/<n>/splits/<n>/stat/zones/<n>/value` | no |
| `pitchIndex` | `/currentPlay/pitchIndex/<n>` | no |
| `playEvents` | `/currentPlay/playEvents/<n>/{details,position,type,...}` | no |
| `allPlays` | `/plays/allPlays/<n>/<anything>` (historical archive) | no |
| `topPerformers` | `/boxscore/topPerformers/<n>/player/stats/...` | no |
| Team aggregates | `/boxscore/teams/<side>/teamStats/{pitching,batting}/<field>` | no |
| Boxscore info | `/boxscore/info/<n>/{label,value}` (text descriptions) | no |
| `gameEvents` / `logicalEvents` | `/metaData/{gameEvents,logicalEvents}/<n>` | no |

~85% of traced paths are decorative. The original plan's "unknown-path-under-`/currentPlay` throws and reseeds" would fire on every poll because decorative paths under `/currentPlay` outnumber modeled ones ~30:1.

## Redundant state channels

MLB routinely publishes the same value through two different paths per poll:

| Field | Primary channel | Duplicate channel |
|---|---|---|
| Balls count | `/currentPlay/count/balls` (140×) | `/linescore/balls` (140×) |
| Strikes count | `/currentPlay/count/strikes` (140×) | `/linescore/strikes` (140×) |
| Outs count | `/currentPlay/count/outs` (37×) | `/linescore/outs` (37×) |
| Current batter | `/currentPlay/matchup/batter/{id,fullName}` | `/linescore/offense/batter/{id,fullName}` |
| Current pitcher | `/currentPlay/matchup/pitcher/{id,fullName}` | `/linescore/defense/pitcher/{id,fullName}` |

Decision: register handlers on the `currentPlay` channel only (matches existing reader), log-and-skip the linescore duplicates.

---

## RFC 6902 op behaviors observed

### `replace` — scalar field update (77%)

Standard case. Fires on every scalar state change (count, inning, runs, player stats). The bulk of useful data moves through here.

### `add` — new element or initialize field (9%)

- `add /boxscore/teams/<side>/pitchers/<n>` — appends a pitcher ID to the bullpen array when a new pitcher enters
- `add /boxscore/teams/<side>/players/IDnew` — adds a full player object when a substitute enters the game
- `add /currentPlay/result/event` + `add /currentPlay/result/description` + `add /currentPlay/result/eventType` — written at end of each play (paired with corresponding `remove` ops at start of next play)
- `add /linescore/offense/<base>` — adds the runner object when a base goes from empty to occupied

### `remove` — null out or delete element (10%)

- `remove /currentPlay/result/{event,description,eventType}` (66×+) — fires between plays to clear the prior result before the next play is added
- `remove /currentPlay/{runners,playEvents}/<n>` — delete historical entries from the currentPlay's arrays
- `remove /linescore/offense/<base>` — clears a base when runner advances past it

### `copy` — **critical finding** (6%)

Majority use: **MLB uses `copy` as a compression trick for bulk zero-initialization of new players' stats.** When a player enters mid-game, MLB emits ~40 copy ops:

```
copy /boxscore/teams/home/players/ID<new>/stats/batting/hits        <- /currentPlay/result/rbi
copy /boxscore/teams/home/players/ID<new>/stats/batting/homeRuns    <- /currentPlay/result/rbi
copy /boxscore/teams/home/players/ID<new>/stats/batting/doubles     <- /currentPlay/result/rbi
copy /boxscore/teams/home/players/ID<new>/stats/batting/atBats      <- /currentPlay/result/rbi
...
```

At copy time, `/currentPlay/result/rbi` is typically `0` or `nil` (pre-play state). The copy sets each stat field to that zero value — semantically equivalent to `{"hits":0, "homeRuns":0, ...}`, but encoded more compactly in RFC 6902.

Implication for our patcher: **safe to skip**. Our formatters (`formatBattingLine`/`formatPitchingLine`) return `nil` when stats are `nil` *or* when `atBats == 0` with no other activity — observable output is identical whether we apply the zero-init copies or ignore them. When the player actually does something, a real `replace` op fires and we update.

Secondary use: **"batter reaches base"** (~16 occurrences in observed slate):
```
copy /linescore/offense/first  <- /plays/allPlays/<n>/matchup/batter
copy /linescore/offense/second <- /plays/allPlays/<n>/matchup/batter
copy /linescore/offense/third  <- /plays/allPlays/<n>/matchup/batter
```

The `from` references a historical play's batter object. We don't model `allPlays`, so we can't read the source value directly. However, MLB appears to also emit scalar `replace /linescore/offense/<base>/id` ops that carry the runner's ID standalone — so we can skip the copy and rely on the accompanying replace.

Tertiary uses: decorative path destinations (pitchIndex, result/isOut, about/hasOut, topPerformers) — all safe to skip.

### `move` — typed runner advance (<1%)

Only meaningful use across 92k rows: runner base-to-base advances on `/linescore/offense/*`.

```
8×  move /linescore/offense/second <- /linescore/offense/first   (1→2)
2×  move /linescore/offense/third  <- /linescore/offense/first   (1→3)
1×  move /linescore/offense/third  <- /linescore/offense/second  (2→3)
```

These are the three cases the typed patcher handles with real code. Every other `move` op targets decorative paths (`pitchIndex`, `runnerIndex`, `playEvents/details/*`, `topPerformers/*`) and falls through to `UnknownPatchLogger`.

---

## Subtree replace behavior

**Phase 1 did not observe any whole-parent replaces** on the subtrees we worried about (`/currentPlay`, `/linescore`, `/currentPlay/count`, `/matchup/batter`, `/matchup/pitcher`). Every state change traveled as leaf-level scalar updates.

The `DiffPatchResult.fullUpdate` branch in [`fetchDiffPatch`](../onDeck/Networking/MLBStatsAPI.swift#L98) still occasionally fires (MLB returns a whole feed object instead of a patch array) but that's handled separately as a full Codable decode — not a subtree patch.

Implication: the original subtree-handler list in FIX-DESIGN was over-prepared. Trimmed to defensive handlers for two cases not yet observed but plausible: `add /boxscore/.../players/IDnew` (new player enters) and `replace /.../stats/{batting,pitching}` (whole stat line).

---

## What this bought us

| Design question | Answer from trace |
|---|---|
| Can we derive the patch-path list from `parseLiveFeedResponse`? | **No.** MLB emits 4,825 unique paths; our reader touches <30. Derivation would over-produce *and* miss some paths we care about |
| Is the "unknown-under-relevant-prefix → throw" classifier safe? | **No.** `/currentPlay` is a decorative-field magnet; the middle tier would reseed constantly |
| Should we implement `copy`/`move` generically? | **No.** 95%+ of copy/move traffic targets paths we don't model. Of the remaining ~5%, most is zero-init compression that's observably safe to skip. Only 3 `move` cases on `/linescore/offense/*` need real handlers |
| Are subtree replaces a real concern? | **Not yet.** Zero observed. Handlers registered defensively but may never fire |
| Will MLB ever surprise us after launch? | Probably. `UnknownPatchLogger` ships permanently for that reason — any frequently-seen unknown path prompts a follow-up handler |

---

## Analysis commands (reproducible)

```bash
trace=~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-diffpatch-trace.csv

# Top unique (op, path) pairs
tail -n +2 "$trace" | awk -F',' '{print $3" "$4}' | sort | uniq -c | sort -rn | head -40

# Unique move destinations + sources
tail -n +2 "$trace" | awk -F',' '$3=="move"{print $4" <- "$5}' | sort | uniq -c | sort -rn

# Copy ops hitting our modeled stat fields (non-rbi sources are the concern)
tail -n +2 "$trace" | awk -F',' '$3=="copy"' \
  | grep -E "/stats/(batting/(atBats|hits|runs|doubles|triples|homeRuns|rbi|baseOnBalls|strikeOuts|stolenBases)|pitching/(inningsPitched|hits|earnedRuns|strikeOuts|baseOnBalls|numberOfPitches))" \
  | awk -F',' '$5 !~ /currentPlay\/result\/rbi$/ {print $4" <- "$5}' \
  | sed -E 's|/ID[0-9]+|/ID<n>|g' | sort | uniq -c | sort -rn
```

---

## References

- [FIX-DESIGN.md](FIX-DESIGN.md) — patcher plan built on these findings
- [DiffPatchTraceLogger.swift](../onDeck/Utilities/DiffPatchTraceLogger.swift) — the Phase 1 instrument
- [MLBStatsAPI.swift](../onDeck/Networking/MLBStatsAPI.swift#L127-L129) — call site
