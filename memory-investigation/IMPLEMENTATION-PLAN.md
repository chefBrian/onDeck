# Typed LiveFeedPatcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the four-step JSON patch pipeline (`JSONSerialization → JSONPatch → reserialize → Codable`) with a typed `LiveFeedPatcher` that mutates `LiveFeedData` fields directly from RFC 6902 ops. Eliminate the Codable decode that [FINDINGS.md](FINDINGS.md) attributes ~26 MB/hr of heap growth to.

**Architecture:** Two-tier dispatch per op — registered handler mutates in place, unregistered op logs to `UnknownPatchLogger` and skips. `latestFeeds` is the single source of truth (absorbs `cachedFeedData` + `cachedTimecodes`). `PlayerGameStats` reshapes from formatted strings to raw typed structs with computed `formatted` properties so field-level patches are one-line mutations.

**Tech Stack:** Swift 6 (strict concurrency), SwiftUI, @MainActor, Foundation only. No new dependencies.

**Evidence base:** [PHASE-1-TRACE.md](PHASE-1-TRACE.md) — 92k ops across 2 slate days. [FIX-DESIGN.md](FIX-DESIGN.md) — design of record.

**Branch:** `memory-probe-2`. Commits land on this branch for local testing; final merge to `main` is gated on a post-fix overnight observation (deferred to a later round).

---

## Testing strategy (no existing test target)

The `onDeck.xcodeproj` has no test target. Rather than add one via pbxproj surgery, tests live as a DEBUG-only self-test class compiled into the app target and executed automatically when the app boots in DEBUG. Pass/fail prints to Console; assertion failures crash via `precondition` so broken patcher code cannot silently ship.

- `onDeck/Utilities/LiveFeedPatcherTests.swift` — `#if DEBUG`-guarded test runner with `static func runAll() -> (passed: Int, failures: [String])`.
- `onDeck/Utilities/LiveFeedPatcherFixtures.swift` — `#if DEBUG`-guarded canned JSON fixtures (minimal feed snapshot + patch sequences).
- Invoked from `onDeckApp.init()` (or a debug-only entry) under `#if DEBUG`. Failures print prefixed `[PatcherTest] FAIL …` and trigger `preconditionFailure`.

Verification command per task: `xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build && build/Build/Products/Debug/onDeck.app/Contents/MacOS/onDeck --run-tests-and-exit` (the `--run-tests-and-exit` flag handling is added in Task 5 as part of the harness so we don't need to launch the UI to run tests).

---

## File Structure

### New files (Commit 1)

| Path | Responsibility |
|---|---|
| `onDeck/Utilities/LiveFeedPatcher.swift` | Pure patcher: `static func apply([[String: Any]], to: inout LiveFeedData)`. Two-tier dispatch. No state, no actors. |
| `onDeck/Utilities/UnknownPatchLogger.swift` | Permanent CSV logger for skipped ops. Rotating 10 MB cap. `@unchecked Sendable` mirroring `DiffPatchTraceLogger`. |
| `onDeck/Utilities/LiveFeedPatcherTests.swift` | `#if DEBUG` self-tests. Equality assertions between patched result and Codable-decoded reference. |
| `onDeck/Utilities/LiveFeedPatcherFixtures.swift` | `#if DEBUG` canned JSON string literals. |

### Modified files (Commit 1)

| Path | Change |
|---|---|
| `onDeck/Networking/MLBStatsAPI.swift` | `LiveFeedData` fields become `var`; add `timeStamp: String?`; `PlayerGameStats` reshapes to `batting/pitching` typed structs with computed `formatted`; promote `FeedBattingStats` / `FeedPitchingStats` to public `PlayerBattingStats` / `PlayerPitchingStats` (Codable + Sendable + Equatable); `LiveFeedData: Equatable`; `parseLiveFeedResponse` populates `timeStamp`; simplify `decodeLiveFeed` signature (drop the tuple timecode return since it's now on the struct). |
| `onDeck/App/onDeckApp.swift` | `#if DEBUG` test runner invocation at init. |

### Modified files (Commit 2)

| Path | Change |
|---|---|
| `onDeck/Managers/GameMonitor.swift` | Delete `cachedFeedData`, `cachedTimecodes`, their dictionary mutations, and `clearCaches()`. Rewire `pollSingleGame` to use `latestFeeds` as source of truth. `.patches` branch uses local-var pattern + `LiveFeedPatcher.apply`. `.fullUpdate` uses Codable decode. Network-error outer catch nulls `latestFeeds[gamePk]?.timeStamp` only. |
| `onDeck/App/AppState.swift` | Replace `gameMonitor.clearCaches()` call with new `gameMonitor.invalidateTimecodes()` (nulls each feed's `timeStamp` without clearing the UI-readable state). Update two view-layer call sites (`stats.battingLine` / `stats.pitchingLine`). |
| `onDeck/Views/MenuBarView.swift` | Update four call sites from `stats.battingLine` / `stats.pitchingLine` to `stats.batting?.formatted` / `stats.pitching?.formatted`. |
| `onDeck/Networking/MLBStatsAPI.swift` | Remove the now-unused inline format helpers (`formatBattingLine`, `formatPitchingLine`) — logic moved to the typed structs in Commit 1. Simplify `parsePlayerStats` to copy raw fields. |

### Deleted files (Commit 2)

| Path | Reason |
|---|---|
| `onDeck/Utilities/JSONPatch.swift` | Replaced by `LiveFeedPatcher`. Silent-drop of copy/move ops is a bug that the typed patcher resolves. |
| `onDeck/Utilities/DiffPatchTraceLogger.swift` | Phase 1 instrument; trace complete. |

---

## Commit 1: Types + patcher + tests (no wiring)

At the end of Commit 1 the app still builds and runs identically — the old pipeline is intact. The new code is parallel infrastructure plus a DEBUG-only self-test that must pass.

### Task 1: Reshape `PlayerGameStats` to typed raw structs

**Files:**
- Modify: `onDeck/Networking/MLBStatsAPI.swift:302-305` (public `PlayerGameStats` struct)
- Modify: `onDeck/Networking/MLBStatsAPI.swift:444-464` (private `FeedBattingStats` / `FeedPitchingStats`)
- Modify: `onDeck/Networking/MLBStatsAPI.swift:196-243` (private `parsePlayerStats` + `formatBattingLine` + `formatPitchingLine`)

- [ ] **Step 1: Delete the private `FeedBattingStats` and `FeedPitchingStats` structs (lines 444-464)**

The new public `PlayerBattingStats` / `PlayerPitchingStats` (next step) replace them — they'll be both Codable (for the initial feed parse) and public (for the patcher to mutate).

- [ ] **Step 2: Replace `PlayerGameStats` (lines 302-305) with the new shape**

```swift
struct PlayerGameStats: Sendable, Equatable, Codable {
    var batting: PlayerBattingStats?
    var pitching: PlayerPitchingStats?
}

struct PlayerBattingStats: Sendable, Equatable, Codable {
    var atBats: Int?
    var hits: Int?
    var runs: Int?
    var doubles: Int?
    var triples: Int?
    var homeRuns: Int?
    var rbi: Int?
    var baseOnBalls: Int?
    var strikeOuts: Int?
    var stolenBases: Int?

    var formatted: String? {
        guard let ab = atBats else { return nil }
        let hasActivity = ab > 0 || (baseOnBalls ?? 0) > 0 || (stolenBases ?? 0) > 0
        guard hasActivity else { return nil }
        var line = "\(hits ?? 0)-\(ab)"
        var extras: [String] = []
        if let v = doubles, v > 0 { extras.append(v > 1 ? "\(v) 2B" : "2B") }
        if let v = triples, v > 0 { extras.append(v > 1 ? "\(v) 3B" : "3B") }
        if let v = homeRuns, v > 0 { extras.append(v > 1 ? "\(v) HR" : "HR") }
        if let v = rbi, v > 0 { extras.append("\(v) RBI") }
        if let v = runs, v > 0 { extras.append("\(v) R") }
        if let v = baseOnBalls, v > 0 { extras.append(v > 1 ? "\(v) BB" : "BB") }
        if let v = stolenBases, v > 0 { extras.append(v > 1 ? "\(v) SB" : "SB") }
        if !extras.isEmpty { line += " · " + extras.joined(separator: ", ") }
        return line
    }
}

struct PlayerPitchingStats: Sendable, Equatable, Codable {
    var inningsPitched: String?
    var hits: Int?
    var earnedRuns: Int?
    var strikeOuts: Int?
    var baseOnBalls: Int?
    var numberOfPitches: Int?

    var formatted: String? {
        guard let ip = inningsPitched, ip != "0.0" else { return nil }
        var parts = ["\(ip) IP"]
        if let k = strikeOuts, k > 0 { parts.append("\(k)K") }
        if let er = earnedRuns { parts.append("\(er)ER") }
        if let np = numberOfPitches, np > 0 { parts.append("\(np)P") }
        return parts.joined(separator: ", ")
    }
}
```

- [ ] **Step 3: Rewrite `parsePlayerStats` (lines 198-217) to copy raw fields**

```swift
private static func parsePlayerStats(boxscore: FeedBoxscore?) -> [Int: PlayerGameStats] {
    guard let boxscore else { return [:] }
    var result: [Int: PlayerGameStats] = [:]

    for teamEntry in [boxscore.teams.home, boxscore.teams.away] {
        guard let players = teamEntry.players else { continue }
        for (key, player) in players {
            guard let idStr = key.hasPrefix("ID") ? String(key.dropFirst(2)) : nil,
                  let id = Int(idStr),
                  let stats = player.stats else { continue }

            let entry = PlayerGameStats(batting: stats.batting, pitching: stats.pitching)
            if entry.batting != nil || entry.pitching != nil {
                result[id] = entry
            }
        }
    }
    return result
}
```

- [ ] **Step 4: Update `FeedBoxscorePlayerStats` (line 439-442) to use the new public types**

```swift
private struct FeedBoxscorePlayerStats: Codable {
    let batting: PlayerBattingStats?
    let pitching: PlayerPitchingStats?
}
```

- [ ] **Step 5: Delete `formatBattingLine` and `formatPitchingLine` (lines 219-243)**

The logic has moved to the `formatted` computed properties on `PlayerBattingStats` / `PlayerPitchingStats`.

- [ ] **Step 6: Update the two internal call sites in `MLBStatsAPI.swift` that referenced the deleted format helpers**

Already handled by Step 3 — `parsePlayerStats` no longer calls them. Verify with:

```bash
grep -n "formatBattingLine\|formatPitchingLine" "/Users/brian/Dev Me/onDeck/onDeck/Networking/MLBStatsAPI.swift"
```

Expected: no matches.

- [ ] **Step 7: Update external call sites temporarily (compile-green only — full rewrite comes in Commit 2)**

Commit 1 must build. Six external call sites reference the old `battingLine` / `pitchingLine` strings:
- `onDeck/Managers/GameMonitor.swift:406`
- `onDeck/App/AppState.swift:379, 381`
- `onDeck/Views/MenuBarView.swift:419, 421, 559, 561`

For each, replace `stats.battingLine` → `stats.batting?.formatted` and `stats.pitchingLine` → `stats.pitching?.formatted`. These are semantically identical — the computed property returns the same string the old stored property held.

Use `Grep` to enumerate exactly, then `Edit` each call site. Build must pass.

- [ ] **Step 8: Build to verify Commit 1 stage is green**

Run: `cd "/Users/brian/Dev Me/onDeck" && xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -20`
Expected: `** BUILD SUCCEEDED **`

### Task 2: Reshape `LiveFeedData` for mutation

**Files:**
- Modify: `onDeck/Networking/MLBStatsAPI.swift:270-300` (public `LiveFeedData`)
- Modify: `onDeck/Networking/MLBStatsAPI.swift:142-180` (`parseLiveFeedResponse`)
- Modify: `onDeck/Networking/MLBStatsAPI.swift:79-88` (`decodeLiveFeed`)

- [ ] **Step 1: Replace `LiveFeedData` (lines 270-300) with mutable + `Equatable` + `timeStamp`**

```swift
struct LiveFeedData: Sendable, Equatable {
    var timeStamp: String?             // from /metaData/timeStamp — replaces cachedTimecodes
    var gameState: String              // "Preview", "Live", "Final"
    var detailedState: String?         // "Pre-Game", "Warmup", "In Progress", etc.
    var currentBatterID: Int?
    var currentBatterName: String?
    var currentPitcherID: Int?
    var currentPitcherName: String?
    var inning: Int?
    var inningHalf: String?
    var inningState: String?
    var homeScore: Int
    var awayScore: Int
    var homeTeam: String
    var awayTeam: String
    var homeTeamID: Int
    var awayTeamID: Int
    var balls: Int
    var strikes: Int
    var outs: Int
    var runnerOnFirst: Int?
    var runnerOnSecond: Int?
    var runnerOnThird: Int?
    var isPlayComplete: Bool
    var lastPlayEvent: String?
    var lastPlayDescription: String?
    var homeBattingOrder: [Int]
    var awayBattingOrder: [Int]
    var homePitchers: [Int]
    var awayPitchers: [Int]
    var playerStats: [Int: PlayerGameStats]
}
```

- [ ] **Step 2: Update `parseLiveFeedResponse` (lines 142-180) to populate `timeStamp`**

First line of the returned `LiveFeedData`: `timeStamp: response.metaData?.timeStamp,`. Rest of the initializer unchanged.

- [ ] **Step 3: Simplify `decodeLiveFeed` signature**

Current signature (search for the definition above line 79):

```swift
static func decodeLiveFeed(from data: Data) throws -> (LiveFeedData, String?)
```

Change to:

```swift
static func decodeLiveFeed(from data: Data) throws -> LiveFeedData
```

Body: drop the separate `timeStamp` tuple — the value is now on the returned struct.

Search for call sites: `grep -n "decodeLiveFeed" "/Users/brian/Dev Me/onDeck/onDeck/"**/*.swift`. Update each to drop the tuple destructuring. The old call sites in `GameMonitor.pollSingleGame` will be fully rewritten in Commit 2; for now just make them compile by reading `.timeStamp` off the returned struct.

- [ ] **Step 4: Build to verify Task 2 is green**

Run: `cd "/Users/brian/Dev Me/onDeck" && xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -20`
Expected: `** BUILD SUCCEEDED **`

### Task 3: Build `UnknownPatchLogger`

**Files:**
- Create: `onDeck/Utilities/UnknownPatchLogger.swift`

This is the permanent log that ships in all builds (mirror of `DiffPatchTraceLogger` minus DEBUG guard).

- [ ] **Step 1: Write the file**

```swift
import Foundation

/// Permanent log of RFC 6902 ops the typed patcher has no handler for.
///
/// Writes to `~/Library/Containers/<bundle-id>/Data/Library/Caches/onDeck-unknown-patches.csv`
/// with a 10 MB rotation cap. Console output prefixed `[LiveFeedPatcher] unknown: …`.
///
/// Reviewed periodically — any frequently-seen path on a field we care about
/// becomes a new patcher handler in a follow-up round.
final class UnknownPatchLogger: @unchecked Sendable {
    static let shared = UnknownPatchLogger()

    private let fileURL: URL
    private let lock = NSLock()
    private let maxBytes: Int = 10 * 1024 * 1024
    private var didWriteHeader = false

    private init() {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first!
        self.fileURL = caches.appendingPathComponent("onDeck-unknown-patches.csv")
    }

    func record(op: String, path: String, from: String?, value: Any?) {
        let timestamp = Self.isoFormatter.string(from: Date())
        let fromField = from ?? ""
        let preview = Self.previewValue(value)
        let row = "\(timestamp),\(Self.escape(op)),\(Self.escape(path)),\(Self.escape(fromField)),\(Self.escape(preview))\n"

        print("[LiveFeedPatcher] unknown: \(op) \(path)\(from.map { " from=\($0)" } ?? "")")

        lock.lock()
        defer { lock.unlock() }
        ensureFileInitialized()
        rotateIfNeeded()
        append(row)
    }

    private func ensureFileInitialized() {
        if didWriteHeader { return }
        let fm = FileManager.default
        if !fm.fileExists(atPath: fileURL.path) {
            let header = "timestamp,op,path,from,value_preview\n"
            fm.createFile(atPath: fileURL.path, contents: header.data(using: .utf8))
        }
        didWriteHeader = true
    }

    private func rotateIfNeeded() {
        let attrs = try? FileManager.default.attributesOfItem(atPath: fileURL.path)
        guard let size = attrs?[.size] as? Int, size >= maxBytes else { return }
        let rotated = fileURL.deletingPathExtension().appendingPathExtension("csv.1")
        try? FileManager.default.removeItem(at: rotated)
        try? FileManager.default.moveItem(at: fileURL, to: rotated)
        didWriteHeader = false
    }

    private func append(_ text: String) {
        guard let data = text.data(using: .utf8) else { return }
        if let handle = try? FileHandle(forWritingTo: fileURL) {
            defer { try? handle.close() }
            try? handle.seekToEnd()
            try? handle.write(contentsOf: data)
        }
    }

    private static func previewValue(_ value: Any?) -> String {
        guard let value else { return "" }
        if value is NSNull { return "null" }
        let rendered: String
        if JSONSerialization.isValidJSONObject(value),
           let data = try? JSONSerialization.data(withJSONObject: value),
           let string = String(data: data, encoding: .utf8) {
            rendered = string
        } else if let string = value as? String {
            rendered = "\"\(string)\""
        } else {
            rendered = "\(value)"
        }
        return String(rendered.prefix(120))
    }

    private static func escape(_ s: String) -> String {
        guard s.contains(",") || s.contains("\"") || s.contains("\n") else { return s }
        let doubled = s.replacingOccurrences(of: "\"", with: "\"\"")
        return "\"\(doubled)\""
    }

    private static let isoFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}
```

- [ ] **Step 2: Build to verify Task 3 is green**

Run: `cd "/Users/brian/Dev Me/onDeck" && xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -20`
Expected: `** BUILD SUCCEEDED **`

### Task 4: Build `LiveFeedPatcher`

**Files:**
- Create: `onDeck/Utilities/LiveFeedPatcher.swift`

The core file. One `switch` expression per registered path, fall-through to `UnknownPatchLogger`.

- [ ] **Step 1: Write the file**

```swift
import Foundation

/// Applies RFC 6902 JSON patches produced by MLB's `/feed/live/diffPatch` endpoint
/// directly to a `LiveFeedData` struct — no JSON object graph, no reserialize, no Codable.
///
/// Two-tier dispatch: registered (opType, path) pairs mutate typed fields; any other
/// op records via `UnknownPatchLogger` and is silently skipped. See
/// `memory-investigation/FIX-DESIGN.md` for the rationale (decorative paths under
/// `/currentPlay` outnumber modeled ones ~30:1, so reseed-on-unknown-under-relevant-prefix
/// would fire every cycle).
enum LiveFeedPatcher {

    /// Apply patches to a working copy, then commit back to `feed`.
    /// Partial state never escapes — callers get either the fully patched struct
    /// or the original on any handler-internal error (there are none currently).
    static func apply(_ ops: [[String: Any]], to feed: inout LiveFeedData) {
        var working = feed
        for op in ops {
            guard let opType = op["op"] as? String,
                  let path = op["path"] as? String else { continue }
            applyOne(
                opType: opType,
                path: path,
                value: op["value"],
                from: op["from"] as? String,
                feed: &working
            )
        }
        feed = working
    }

    // MARK: - Dispatch

    private static func applyOne(
        opType: String,
        path: String,
        value: Any?,
        from: String?,
        feed: inout LiveFeedData
    ) {
        // Registered scalar leaves (replace-only unless noted)
        switch (opType, path) {

        // --- metaData
        case ("replace", "/metaData/timeStamp"), ("add", "/metaData/timeStamp"):
            feed.timeStamp = value as? String
            return

        // --- gameData/status
        case ("replace", "/gameData/status/abstractGameState"):
            if let s = value as? String { feed.gameState = s }
            return
        case ("replace", "/gameData/status/detailedState"),
             ("add", "/gameData/status/detailedState"):
            feed.detailedState = value as? String
            return
        case ("remove", "/gameData/status/detailedState"):
            feed.detailedState = nil
            return

        // --- gameData/teams  (rare — name/id shouldn't change mid-game, but register defensively)
        case ("replace", "/gameData/teams/home/name"):
            if let s = value as? String { feed.homeTeam = s }
            return
        case ("replace", "/gameData/teams/home/id"):
            if let n = intValue(value) { feed.homeTeamID = n }
            return
        case ("replace", "/gameData/teams/away/name"):
            if let s = value as? String { feed.awayTeam = s }
            return
        case ("replace", "/gameData/teams/away/id"):
            if let n = intValue(value) { feed.awayTeamID = n }
            return

        // --- currentPlay / matchup
        case ("replace", "/liveData/plays/currentPlay/matchup/batter/id"),
             ("add", "/liveData/plays/currentPlay/matchup/batter/id"):
            feed.currentBatterID = intValue(value)
            return
        case ("replace", "/liveData/plays/currentPlay/matchup/batter/fullName"),
             ("add", "/liveData/plays/currentPlay/matchup/batter/fullName"):
            feed.currentBatterName = value as? String
            return
        case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/id"),
             ("add", "/liveData/plays/currentPlay/matchup/pitcher/id"):
            feed.currentPitcherID = intValue(value)
            return
        case ("replace", "/liveData/plays/currentPlay/matchup/pitcher/fullName"),
             ("add", "/liveData/plays/currentPlay/matchup/pitcher/fullName"):
            feed.currentPitcherName = value as? String
            return

        // --- currentPlay / about
        case ("replace", "/liveData/plays/currentPlay/about/isComplete"),
             ("add", "/liveData/plays/currentPlay/about/isComplete"):
            feed.isPlayComplete = (value as? Bool) ?? feed.isPlayComplete
            return

        // --- currentPlay / result
        case ("replace", "/liveData/plays/currentPlay/result/event"),
             ("add", "/liveData/plays/currentPlay/result/event"):
            feed.lastPlayEvent = value as? String
            return
        case ("remove", "/liveData/plays/currentPlay/result/event"):
            feed.lastPlayEvent = nil
            return
        case ("replace", "/liveData/plays/currentPlay/result/description"),
             ("add", "/liveData/plays/currentPlay/result/description"):
            feed.lastPlayDescription = value as? String
            return
        case ("remove", "/liveData/plays/currentPlay/result/description"):
            feed.lastPlayDescription = nil
            return

        // --- currentPlay / count
        case ("replace", "/liveData/plays/currentPlay/count/balls"),
             ("add", "/liveData/plays/currentPlay/count/balls"):
            feed.balls = intValue(value) ?? feed.balls
            return
        case ("replace", "/liveData/plays/currentPlay/count/strikes"),
             ("add", "/liveData/plays/currentPlay/count/strikes"):
            feed.strikes = intValue(value) ?? feed.strikes
            return
        case ("replace", "/liveData/plays/currentPlay/count/outs"),
             ("add", "/liveData/plays/currentPlay/count/outs"):
            feed.outs = intValue(value) ?? feed.outs
            return

        // --- linescore
        case ("replace", "/liveData/linescore/currentInning"),
             ("add", "/liveData/linescore/currentInning"):
            feed.inning = intValue(value)
            return
        case ("replace", "/liveData/linescore/inningHalf"),
             ("add", "/liveData/linescore/inningHalf"):
            feed.inningHalf = value as? String
            return
        case ("replace", "/liveData/linescore/inningState"),
             ("add", "/liveData/linescore/inningState"):
            feed.inningState = value as? String
            return
        case ("replace", "/liveData/linescore/teams/home/runs"),
             ("add", "/liveData/linescore/teams/home/runs"):
            feed.homeScore = intValue(value) ?? feed.homeScore
            return
        case ("replace", "/liveData/linescore/teams/away/runs"),
             ("add", "/liveData/linescore/teams/away/runs"):
            feed.awayScore = intValue(value) ?? feed.awayScore
            return

        // --- linescore / offense — scalar runner IDs
        case ("replace", "/liveData/linescore/offense/first/id"),
             ("add", "/liveData/linescore/offense/first/id"):
            feed.runnerOnFirst = intValue(value)
            return
        case ("replace", "/liveData/linescore/offense/second/id"),
             ("add", "/liveData/linescore/offense/second/id"):
            feed.runnerOnSecond = intValue(value)
            return
        case ("replace", "/liveData/linescore/offense/third/id"),
             ("add", "/liveData/linescore/offense/third/id"):
            feed.runnerOnThird = intValue(value)
            return
        case ("remove", "/liveData/linescore/offense/first"),
             ("remove", "/liveData/linescore/offense/first/id"):
            feed.runnerOnFirst = nil
            return
        case ("remove", "/liveData/linescore/offense/second"),
             ("remove", "/liveData/linescore/offense/second/id"):
            feed.runnerOnSecond = nil
            return
        case ("remove", "/liveData/linescore/offense/third"),
             ("remove", "/liveData/linescore/offense/third/id"):
            feed.runnerOnThird = nil
            return

        // --- linescore / offense — typed runner advance (move ops; see PHASE-1-TRACE)
        case ("move", "/liveData/linescore/offense/second"):
            if from == "/liveData/linescore/offense/first" {
                feed.runnerOnSecond = feed.runnerOnFirst
                feed.runnerOnFirst = nil
                return
            }
        case ("move", "/liveData/linescore/offense/third"):
            if from == "/liveData/linescore/offense/first" {
                feed.runnerOnThird = feed.runnerOnFirst
                feed.runnerOnFirst = nil
                return
            }
            if from == "/liveData/linescore/offense/second" {
                feed.runnerOnThird = feed.runnerOnSecond
                feed.runnerOnSecond = nil
                return
            }

        default:
            break
        }

        // Prefix-dispatched handlers (lineup arrays, player boxscore)
        if tryApplyBoxscoreArrayPatch(opType: opType, path: path, value: value, feed: &feed) { return }
        if tryApplyPlayerStatsPatch(opType: opType, path: path, value: value, feed: &feed) { return }

        // Fallthrough: unknown op — log and skip
        UnknownPatchLogger.shared.record(op: opType, path: path, from: from, value: value)
    }

    // MARK: - Boxscore array patches (batting orders, pitcher lists)

    private static func tryApplyBoxscoreArrayPatch(
        opType: String, path: String, value: Any?, feed: inout LiveFeedData
    ) -> Bool {
        for (side, keyPath) in [
            ("home", \LiveFeedData.homeBattingOrder),
            ("away", \LiveFeedData.awayBattingOrder),
        ] as [(String, WritableKeyPath<LiveFeedData, [Int]>)] {
            let base = "/liveData/boxscore/teams/\(side)/battingOrder"
            if path == base {
                if let arr = value as? [Int] { feed[keyPath: keyPath] = arr }
                else if let arr = value as? [Any] { feed[keyPath: keyPath] = arr.compactMap { intValue($0) } }
                return true
            }
            if path.hasPrefix(base + "/") {
                let suffix = String(path.dropFirst(base.count + 1))
                if suffix == "-", let n = intValue(value), opType == "add" {
                    feed[keyPath: keyPath].append(n)
                    return true
                }
                if let idx = Int(suffix) {
                    var arr = feed[keyPath: keyPath]
                    switch opType {
                    case "replace":
                        if idx < arr.count, let n = intValue(value) { arr[idx] = n }
                    case "add":
                        if let n = intValue(value) {
                            if idx <= arr.count { arr.insert(n, at: idx) } else { arr.append(n) }
                        }
                    case "remove":
                        if idx < arr.count { arr.remove(at: idx) }
                    default:
                        return false
                    }
                    feed[keyPath: keyPath] = arr
                    return true
                }
            }
        }

        for (side, keyPath) in [
            ("home", \LiveFeedData.homePitchers),
            ("away", \LiveFeedData.awayPitchers),
        ] as [(String, WritableKeyPath<LiveFeedData, [Int]>)] {
            let base = "/liveData/boxscore/teams/\(side)/pitchers"
            if path == base {
                if let arr = value as? [Int] { feed[keyPath: keyPath] = arr }
                else if let arr = value as? [Any] { feed[keyPath: keyPath] = arr.compactMap { intValue($0) } }
                return true
            }
            if path.hasPrefix(base + "/") {
                let suffix = String(path.dropFirst(base.count + 1))
                if suffix == "-", let n = intValue(value), opType == "add" {
                    feed[keyPath: keyPath].append(n)
                    return true
                }
                if let idx = Int(suffix) {
                    var arr = feed[keyPath: keyPath]
                    switch opType {
                    case "replace":
                        if idx < arr.count, let n = intValue(value) { arr[idx] = n }
                    case "add":
                        if let n = intValue(value) {
                            if idx <= arr.count { arr.insert(n, at: idx) } else { arr.append(n) }
                        }
                    case "remove":
                        if idx < arr.count { arr.remove(at: idx) }
                    default:
                        return false
                    }
                    feed[keyPath: keyPath] = arr
                    return true
                }
            }
        }

        return false
    }

    // MARK: - Player stats patches

    /// Recognises paths like:
    ///   /liveData/boxscore/teams/<side>/players/ID<n>
    ///   /liveData/boxscore/teams/<side>/players/ID<n>/stats/batting
    ///   /liveData/boxscore/teams/<side>/players/ID<n>/stats/batting/<field>
    ///   (and /stats/pitching/*)
    private static func tryApplyPlayerStatsPatch(
        opType: String, path: String, value: Any?, feed: inout LiveFeedData
    ) -> Bool {
        let prefixes = [
            "/liveData/boxscore/teams/home/players/ID",
            "/liveData/boxscore/teams/away/players/ID",
        ]
        for prefix in prefixes {
            guard path.hasPrefix(prefix) else { continue }
            let suffix = String(path.dropFirst(prefix.count))

            let (idString, rest): (String, String) = {
                if let slash = suffix.firstIndex(of: "/") {
                    return (String(suffix[..<slash]), String(suffix[slash...]))  // rest starts with "/"
                }
                return (suffix, "")
            }()
            guard let id = Int(idString) else { return false }

            // Full player add (new player enters game)
            if opType == "add" && rest.isEmpty {
                if let dict = value as? [String: Any],
                   let stats = decodePlayerStats(from: dict) {
                    feed.playerStats[id] = stats
                }
                return true
            }

            if opType == "remove" && rest.isEmpty {
                feed.playerStats.removeValue(forKey: id)
                return true
            }

            // /stats/batting or /stats/pitching subtree
            if rest == "/stats/batting" {
                var entry = feed.playerStats[id] ?? PlayerGameStats()
                if let dict = value as? [String: Any] {
                    entry.batting = decodeBatting(dict)
                } else if opType == "remove" {
                    entry.batting = nil
                }
                feed.playerStats[id] = entry
                return true
            }
            if rest == "/stats/pitching" {
                var entry = feed.playerStats[id] ?? PlayerGameStats()
                if let dict = value as? [String: Any] {
                    entry.pitching = decodePitching(dict)
                } else if opType == "remove" {
                    entry.pitching = nil
                }
                feed.playerStats[id] = entry
                return true
            }

            // /stats/batting/<field>
            if rest.hasPrefix("/stats/batting/") {
                let field = String(rest.dropFirst("/stats/batting/".count))
                var entry = feed.playerStats[id] ?? PlayerGameStats()
                var b = entry.batting ?? PlayerBattingStats()
                applyBattingField(field: field, opType: opType, value: value, into: &b)
                entry.batting = b
                feed.playerStats[id] = entry
                return true
            }
            if rest.hasPrefix("/stats/pitching/") {
                let field = String(rest.dropFirst("/stats/pitching/".count))
                var entry = feed.playerStats[id] ?? PlayerGameStats()
                var p = entry.pitching ?? PlayerPitchingStats()
                applyPitchingField(field: field, opType: opType, value: value, into: &p)
                entry.pitching = p
                feed.playerStats[id] = entry
                return true
            }

            // Other player subtrees (person, position, seasonStats, etc.) — not modeled
            return false
        }
        return false
    }

    private static func applyBattingField(field: String, opType: String, value: Any?, into b: inout PlayerBattingStats) {
        let n: Int? = (opType == "remove") ? nil : intValue(value)
        switch field {
        case "atBats": b.atBats = n
        case "hits": b.hits = n
        case "runs": b.runs = n
        case "doubles": b.doubles = n
        case "triples": b.triples = n
        case "homeRuns": b.homeRuns = n
        case "rbi": b.rbi = n
        case "baseOnBalls": b.baseOnBalls = n
        case "strikeOuts": b.strikeOuts = n
        case "stolenBases": b.stolenBases = n
        default: break  // Decorative batting field — ignored.
        }
    }

    private static func applyPitchingField(field: String, opType: String, value: Any?, into p: inout PlayerPitchingStats) {
        switch field {
        case "inningsPitched":
            p.inningsPitched = (opType == "remove") ? nil : (value as? String)
        case "hits":
            p.hits = (opType == "remove") ? nil : intValue(value)
        case "earnedRuns":
            p.earnedRuns = (opType == "remove") ? nil : intValue(value)
        case "strikeOuts":
            p.strikeOuts = (opType == "remove") ? nil : intValue(value)
        case "baseOnBalls":
            p.baseOnBalls = (opType == "remove") ? nil : intValue(value)
        case "numberOfPitches":
            p.numberOfPitches = (opType == "remove") ? nil : intValue(value)
        default: break  // Decorative pitching field — ignored.
        }
    }

    private static func decodePlayerStats(from playerDict: [String: Any]) -> PlayerGameStats? {
        guard let stats = playerDict["stats"] as? [String: Any] else { return nil }
        let batting = (stats["batting"] as? [String: Any]).flatMap { decodeBatting($0) }
        let pitching = (stats["pitching"] as? [String: Any]).flatMap { decodePitching($0) }
        if batting == nil && pitching == nil { return nil }
        return PlayerGameStats(batting: batting, pitching: pitching)
    }

    private static func decodeBatting(_ d: [String: Any]) -> PlayerBattingStats {
        PlayerBattingStats(
            atBats: intValue(d["atBats"]),
            hits: intValue(d["hits"]),
            runs: intValue(d["runs"]),
            doubles: intValue(d["doubles"]),
            triples: intValue(d["triples"]),
            homeRuns: intValue(d["homeRuns"]),
            rbi: intValue(d["rbi"]),
            baseOnBalls: intValue(d["baseOnBalls"]),
            strikeOuts: intValue(d["strikeOuts"]),
            stolenBases: intValue(d["stolenBases"])
        )
    }

    private static func decodePitching(_ d: [String: Any]) -> PlayerPitchingStats {
        PlayerPitchingStats(
            inningsPitched: d["inningsPitched"] as? String,
            hits: intValue(d["hits"]),
            earnedRuns: intValue(d["earnedRuns"]),
            strikeOuts: intValue(d["strikeOuts"]),
            baseOnBalls: intValue(d["baseOnBalls"]),
            numberOfPitches: intValue(d["numberOfPitches"])
        )
    }

    // MARK: - Helpers

    private static func intValue(_ value: Any?) -> Int? {
        if let n = value as? Int { return n }
        if let n = value as? Double { return Int(n) }
        if let n = value as? NSNumber { return n.intValue }
        if let s = value as? String { return Int(s) }
        return nil
    }
}
```

- [ ] **Step 2: Add `PlayerGameStats` default init**

The patcher constructs `PlayerGameStats()` with no args. Swift auto-synthesises that only when every `var` has a default. Go back to `MLBStatsAPI.swift` and give each stored property `= nil` if not already:

```swift
struct PlayerGameStats: Sendable, Equatable, Codable {
    var batting: PlayerBattingStats? = nil
    var pitching: PlayerPitchingStats? = nil
}

struct PlayerBattingStats: Sendable, Equatable, Codable {
    var atBats: Int? = nil
    var hits: Int? = nil
    // ... etc for all fields
}

struct PlayerPitchingStats: Sendable, Equatable, Codable {
    var inningsPitched: String? = nil
    var hits: Int? = nil
    // ... etc for all fields
}
```

- [ ] **Step 3: Build to verify Task 4 is green**

Run: `cd "/Users/brian/Dev Me/onDeck" && xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -20`
Expected: `** BUILD SUCCEEDED **`

### Task 5: Build DEBUG self-test harness

**Files:**
- Create: `onDeck/Utilities/LiveFeedPatcherFixtures.swift`
- Create: `onDeck/Utilities/LiveFeedPatcherTests.swift`
- Modify: `onDeck/App/onDeckApp.swift` (add DEBUG test invocation)

- [ ] **Step 1: Create minimal fixtures**

`onDeck/Utilities/LiveFeedPatcherFixtures.swift`:

```swift
#if DEBUG
import Foundation

/// Minimal captured fixtures for `LiveFeedPatcherTests`.
/// Fixtures are small by design — we're testing dispatch correctness, not volume.
enum LiveFeedPatcherFixtures {

    /// Minimal canonical feed — just enough shape for parse + patch round-trips.
    static let baseFeedJSON: String = """
    {
      "metaData": {"timeStamp": "20260416_180000"},
      "gameData": {
        "status": {"abstractGameState": "Live", "detailedState": "In Progress"},
        "teams": {
          "away": {"id": 111, "name": "Away"},
          "home": {"id": 222, "name": "Home"}
        }
      },
      "liveData": {
        "plays": {
          "currentPlay": {
            "about": {"isComplete": false},
            "matchup": {
              "batter": {"id": 1, "fullName": "Batter One"},
              "pitcher": {"id": 2, "fullName": "Pitcher Two"}
            },
            "count": {"balls": 0, "strikes": 0, "outs": 0}
          }
        },
        "linescore": {
          "currentInning": 1,
          "inningHalf": "Top",
          "inningState": "Top",
          "teams": {
            "home": {"runs": 0},
            "away": {"runs": 0}
          }
        },
        "boxscore": {
          "teams": {
            "home": {
              "battingOrder": [],
              "pitchers": [2],
              "players": {
                "ID2": {"stats": {"pitching": {"inningsPitched": "0.0"}}}
              }
            },
            "away": {
              "battingOrder": [1],
              "pitchers": [],
              "players": {
                "ID1": {"stats": {"batting": {"atBats": 0}}}
              }
            }
          }
        }
      }
    }
    """

    /// Feed after a single plate appearance ends with a 2-run HR.
    /// Equivalent terminal state for `scalarReplaces` patch below.
    static let afterScalarReplacesJSON: String = """
    {
      "metaData": {"timeStamp": "20260416_180010"},
      "gameData": {
        "status": {"abstractGameState": "Live", "detailedState": "In Progress"},
        "teams": {
          "away": {"id": 111, "name": "Away"},
          "home": {"id": 222, "name": "Home"}
        }
      },
      "liveData": {
        "plays": {
          "currentPlay": {
            "about": {"isComplete": true},
            "matchup": {
              "batter": {"id": 1, "fullName": "Batter One"},
              "pitcher": {"id": 2, "fullName": "Pitcher Two"}
            },
            "count": {"balls": 3, "strikes": 2, "outs": 0},
            "result": {"event": "Home Run", "description": "Batter One hits a 2-run HR"}
          }
        },
        "linescore": {
          "currentInning": 1,
          "inningHalf": "Top",
          "inningState": "Top",
          "teams": {
            "home": {"runs": 0},
            "away": {"runs": 2}
          }
        },
        "boxscore": {
          "teams": {
            "home": {
              "battingOrder": [],
              "pitchers": [2],
              "players": {
                "ID2": {"stats": {"pitching": {"inningsPitched": "0.0", "earnedRuns": 2, "hits": 1, "numberOfPitches": 6}}}
              }
            },
            "away": {
              "battingOrder": [1],
              "pitchers": [],
              "players": {
                "ID1": {"stats": {"batting": {"atBats": 1, "hits": 1, "homeRuns": 1, "rbi": 2, "runs": 1}}}
              }
            }
          }
        }
      }
    }
    """

    /// Scalar-leaf patches — the 75% case from Phase 1.
    static let scalarReplacesPatch: [[String: Any]] = [
        ["op": "replace", "path": "/metaData/timeStamp", "value": "20260416_180010"],
        ["op": "add", "path": "/liveData/plays/currentPlay/result/event", "value": "Home Run"],
        ["op": "add", "path": "/liveData/plays/currentPlay/result/description", "value": "Batter One hits a 2-run HR"],
        ["op": "replace", "path": "/liveData/plays/currentPlay/about/isComplete", "value": true],
        ["op": "replace", "path": "/liveData/plays/currentPlay/count/balls", "value": 3],
        ["op": "replace", "path": "/liveData/plays/currentPlay/count/strikes", "value": 2],
        ["op": "replace", "path": "/liveData/linescore/teams/away/runs", "value": 2],
        ["op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/atBats", "value": 1],
        ["op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/hits", "value": 1],
        ["op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/homeRuns", "value": 1],
        ["op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/rbi", "value": 2],
        ["op": "replace", "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/runs", "value": 1],
        ["op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/hits", "value": 1],
        ["op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/earnedRuns", "value": 2],
        ["op": "replace", "path": "/liveData/boxscore/teams/home/players/ID2/stats/pitching/numberOfPitches", "value": 6],
    ]

    /// `move` on offense — runner advance from first to second.
    static let runnerMoveFirstToSecondPatch: [[String: Any]] = [
        ["op": "move", "from": "/liveData/linescore/offense/first", "path": "/liveData/linescore/offense/second"]
    ]

    /// Decorative path — must be logged and skipped, not throw.
    static let decorativePatch: [[String: Any]] = [
        ["op": "replace", "path": "/liveData/plays/currentPlay/playEvents/0/details/code", "value": "F"]
    ]
}
#endif
```

- [ ] **Step 2: Create the test runner**

`onDeck/Utilities/LiveFeedPatcherTests.swift`:

```swift
#if DEBUG
import Foundation

/// DEBUG-only self-tests for `LiveFeedPatcher`. Invoked at app launch;
/// failures trigger `preconditionFailure` so broken patcher code cannot ship.
///
/// This is not XCTest — the project has no test target. It's a sequence of
/// assertions that run in-process in under ~100 ms.
enum LiveFeedPatcherTests {

    static func runAll() {
        var failures: [String] = []

        test(&failures, "scalar replace round-trip equals Codable decode") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            LiveFeedPatcher.apply(LiveFeedPatcherFixtures.scalarReplacesPatch, to: &feed)

            let expected = try decodeFeed(json: LiveFeedPatcherFixtures.afterScalarReplacesJSON)
            return feed == expected
                ? nil
                : "patched state != Codable reference (diff in patched struct)"
        }

        test(&failures, "runner move 1->2 transfers ID and clears first") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.runnerOnFirst = 99
            feed.runnerOnSecond = nil

            LiveFeedPatcher.apply(LiveFeedPatcherFixtures.runnerMoveFirstToSecondPatch, to: &feed)

            return (feed.runnerOnFirst == nil && feed.runnerOnSecond == 99)
                ? nil
                : "runnerOnFirst=\(String(describing: feed.runnerOnFirst)) runnerOnSecond=\(String(describing: feed.runnerOnSecond))"
        }

        test(&failures, "decorative path is skipped (no crash, no state change)") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let before = feed
            LiveFeedPatcher.apply(LiveFeedPatcherFixtures.decorativePatch, to: &feed)
            return (feed == before) ? nil : "decorative patch mutated state"
        }

        test(&failures, "zero-init copy into modeled stat field is skipped safely") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let before = feed
            let zeroInit: [[String: Any]] = [
                ["op": "copy",
                 "from": "/liveData/plays/currentPlay/result/rbi",
                 "path": "/liveData/boxscore/teams/away/players/ID1/stats/batting/hits"]
            ]
            LiveFeedPatcher.apply(zeroInit, to: &feed)
            return (feed == before) ? nil : "zero-init copy mutated state"
        }

        test(&failures, "battingOrder array replace") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let p: [[String: Any]] = [
                ["op": "replace", "path": "/liveData/boxscore/teams/home/battingOrder", "value": [10, 11, 12]]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.homeBattingOrder == [10, 11, 12]) ? nil : "\(feed.homeBattingOrder)"
        }

        test(&failures, "pitcher array append via /-") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let p: [[String: Any]] = [
                ["op": "add", "path": "/liveData/boxscore/teams/home/pitchers/-", "value": 9999]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.homePitchers == [2, 9999]) ? nil : "\(feed.homePitchers)"
        }

        test(&failures, "formatted batting line matches legacy output") {
            var b = PlayerBattingStats()
            b.atBats = 4
            b.hits = 2
            b.homeRuns = 1
            b.rbi = 2
            b.runs = 1
            let expected = "2-4 · HR, 2 RBI, 1 R"
            return (b.formatted == expected) ? nil : "got '\(b.formatted ?? "nil")'"
        }

        test(&failures, "formatted pitching line matches legacy output") {
            var p = PlayerPitchingStats()
            p.inningsPitched = "6.1"
            p.strikeOuts = 7
            p.earnedRuns = 2
            p.numberOfPitches = 98
            let expected = "6.1 IP, 7K, 2ER, 98P"
            return (p.formatted == expected) ? nil : "got '\(p.formatted ?? "nil")'"
        }

        if failures.isEmpty {
            print("[PatcherTest] all tests passed")
        } else {
            for f in failures { print("[PatcherTest] FAIL \(f)") }
            preconditionFailure("LiveFeedPatcher tests failed: see console")
        }
    }

    private static func test(_ failures: inout [String], _ name: String, _ body: () throws -> String?) {
        do {
            if let reason = try body() {
                failures.append("\(name): \(reason)")
            }
        } catch {
            failures.append("\(name) threw: \(error)")
        }
    }

    private static func decodeFeed(json: String) throws -> LiveFeedData {
        let data = Data(json.utf8)
        return try MLBStatsAPI.decodeLiveFeed(from: data)
    }
}
#endif
```

- [ ] **Step 3: Wire the self-test into app launch**

Locate the app entry point:

```bash
grep -l "@main" "/Users/brian/Dev Me/onDeck/onDeck/App/"
```

Open that file and add a DEBUG-only test invocation in `init()` (create one if absent). The existing entry likely declares `struct onDeckApp: App`:

```swift
init() {
    #if DEBUG
    LiveFeedPatcherTests.runAll()
    #endif
    // ... existing init code
}
```

If there's no `init()`, add one.

- [ ] **Step 4: Build and launch to verify all tests pass**

Run:
```bash
cd "/Users/brian/Dev Me/onDeck" && \
xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -5
```
Expected: `** BUILD SUCCEEDED **`

Then launch the app once (the tests print to stdout at startup; you can run the binary directly):
```bash
build/Build/Products/Debug/onDeck.app/Contents/MacOS/onDeck 2>&1 | grep -E "^\[PatcherTest\]" | head -20
```
Expected: `[PatcherTest] all tests passed`. If any test fails the app will `preconditionFailure` and crash — fix the patcher until clean.

Kill the app (`pkill -x onDeck`) once verified.

- [ ] **Step 5: Commit 1**

Stage, commit. No co-author line per CLAUDE.md.

```bash
cd "/Users/brian/Dev Me/onDeck"
git add onDeck/Utilities/LiveFeedPatcher.swift \
        onDeck/Utilities/UnknownPatchLogger.swift \
        onDeck/Utilities/LiveFeedPatcherTests.swift \
        onDeck/Utilities/LiveFeedPatcherFixtures.swift \
        onDeck/Networking/MLBStatsAPI.swift \
        onDeck/App/onDeckApp.swift \
        onDeck/Managers/GameMonitor.swift \
        onDeck/App/AppState.swift \
        onDeck/Views/MenuBarView.swift \
        memory-investigation/IMPLEMENTATION-PLAN.md
git commit -m "add typed LiveFeedPatcher with DEBUG self-tests"
```

Expected: commit succeeds. `git status` clean.

---

## Commit 2: Wire patcher into GameMonitor + delete old pipeline

At the end of Commit 2 the app is on the new pipeline. The old JSONPatch + Codable round-trip is gone.

### Task 6: Rewire `GameMonitor.pollSingleGame`

**Files:**
- Modify: `onDeck/Managers/GameMonitor.swift` — delete the two cache dicts (lines 22-23), their mutation in `stopMonitoring` (lines 93-94, 115-116) and `stopMonitoring(gamePk:)`, and rewrite `pollSingleGame` + `pollCycle`.

- [ ] **Step 1: Delete cache properties and their uses in `stopMonitoring` (full stop)**

Remove lines 22-23 (property declarations) and lines 93-94 inside `stopMonitoring()`.

- [ ] **Step 2: Replace `clearCaches()` (lines 106-110) with `invalidateTimecodes()`**

```swift
/// Nulls each cached feed's timeStamp so the next poll cycle does a full
/// fetch per game. Used after system wake when stored timecodes are stale.
/// Preserves the rest of each `LiveFeedData` so the UI keeps rendering
/// last-known state during the round trip.
func invalidateTimecodes() {
    for key in latestFeeds.keys {
        latestFeeds[key]?.timeStamp = nil
    }
    print("[GameMonitor] Timecodes invalidated (stale — next poll does full fetch per game)")
}
```

- [ ] **Step 3: Update `stopMonitoring(gamePk:)` (lines 113-130) — remove cache dict mutations**

Delete the two `cachedFeedData.removeValue` / `cachedTimecodes.removeValue` lines (115-116).

Note: `latestFeeds[gamePk]` is still intentionally retained for the Done section per existing comment.

- [ ] **Step 4: Rewrite `pollCycle` (lines 162-202) to drop the cached-data prefetch**

The `group.addTask` block currently reads `cachedFeedData[gamePk]` and `cachedTimecodes[gamePk]` and passes them to `pollSingleGame`. Replace with:

```swift
await withTaskGroup(of: Void.self) { group in
    for gamePk in gamesToPoll {
        guard let game = monitoredGames[gamePk] else { continue }
        group.addTask { [weak self] in
            guard let self else { return }
            await self.pollSingleGame(gamePk: gamePk, game: game)
        }
    }
}
```

- [ ] **Step 5: Rewrite `pollSingleGame` (lines 204-257)**

```swift
private func pollSingleGame(gamePk: Int, game: Game) async {
    let label = "\(TeamMapping.abbreviation(for: game.awayTeam))@\(TeamMapping.abbreviation(for: game.homeTeam))"
    do {
        let feed: LiveFeedData

        if let existing = latestFeeds[gamePk], let timecode = existing.timeStamp {
            let result = try await mlbAPI.fetchDiffPatch(gamePk: gamePk, since: timecode, label: label)

            switch result {
            case .noChanges:
                return

            case .patches(let patches):
                var working = existing
                LiveFeedPatcher.apply(patches, to: &working)
                latestFeeds[gamePk] = working
                feed = working

            case .fullUpdate(let rawData):
                let decoded = try MLBStatsAPI.decodeLiveFeed(from: rawData)
                latestFeeds[gamePk] = decoded
                feed = decoded
            }
        } else {
            // No seed — full fetch
            let (decoded, _) = try await mlbAPI.fetchLiveFeedRaw(gamePk: gamePk, label: label)
            latestFeeds[gamePk] = decoded
            feed = decoded
        }

        processFeed(feed, gamePk: gamePk, game: game)

        if feed.gameState == "Final" {
            let playerIDsInGame = rosterPlayerIDs.filter { id in
                isPlayerInGame(playerID: id, game: game)
            }
            print("[GameMonitor] Game \(gamePk) is Final - marking done: \(playerIDsInGame)")
            stateManager?.setGameOver(playerIDs: Array(playerIDsInGame), gamePk: gamePk)
            stopMonitoring(gamePk: gamePk)
        }
    } catch {
        // Transient error — preserve last-known feed for UI continuity, but
        // null its timeStamp so the next cycle does a full fetch.
        latestFeeds[gamePk]?.timeStamp = nil
        print("[GameMonitor] Error for game \(gamePk): \(error) - will full-fetch next cycle")
    }
}
```

Note: `fetchLiveFeedRaw` currently returns `(LiveFeedData, Data, String?)`. After Task 2 in Commit 1 simplified `decodeLiveFeed`, we can likewise simplify `fetchLiveFeedRaw` to return just `LiveFeedData` (or keep its signature and discard the extra returns). Pick the simpler option: update `fetchLiveFeedRaw` to `-> LiveFeedData` and adjust both call sites (there's one in `pollSingleGame`, check for others).

- [ ] **Step 6: Update `AppState.swift:545` — replace `clearCaches()` call**

```bash
grep -n "clearCaches" "/Users/brian/Dev Me/onDeck/onDeck/App/AppState.swift"
```

Change the line to `gameMonitor.invalidateTimecodes()`.

- [ ] **Step 7: Simplify `MLBStatsAPI.fetchLiveFeedRaw`**

Find the function:
```bash
grep -n "fetchLiveFeedRaw" "/Users/brian/Dev Me/onDeck/onDeck/Networking/MLBStatsAPI.swift"
```

Change its return type from `-> (LiveFeedData, Data, String?)` to `-> LiveFeedData`. Its body likely has:

```swift
let (data, _) = try await URLSession.shared.data(from: url)
let (decoded, timecode) = try Self.decodeLiveFeed(from: data)
return (decoded, data, timecode)
```

Become:

```swift
let (data, _) = try await URLSession.shared.data(from: url)
return try Self.decodeLiveFeed(from: data)
```

Update all call sites (should be just the one in the rewritten `pollSingleGame`).

- [ ] **Step 8: Build**

Run: `cd "/Users/brian/Dev Me/onDeck" && xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -10`
Expected: `** BUILD SUCCEEDED **`

If new errors appear (e.g. other call sites of `fetchLiveFeedRaw` or `decodeLiveFeed` not caught in the grep), fix in place.

- [ ] **Step 9: Launch and verify in-app patcher tests still pass + real games still poll**

```bash
pkill -x onDeck 2>/dev/null; \
cd "/Users/brian/Dev Me/onDeck" && \
xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -3 && \
build/Build/Products/Debug/onDeck.app/Contents/MacOS/onDeck &
```

Within 30s, check Console:
```bash
sleep 15 && tail -30 /tmp/onDeck.log 2>/dev/null || true
```

Look for:
- `[PatcherTest] all tests passed`
- `[MLB API] GET /diffPatch` entries — not `(unparseable)` or errors
- No crashes / preconditionFailures

Kill the app: `pkill -x onDeck`.

### Task 7: Delete the old pipeline

**Files:**
- Delete: `onDeck/Utilities/JSONPatch.swift`
- Delete: `onDeck/Utilities/DiffPatchTraceLogger.swift`
- Modify: `onDeck/Networking/MLBStatsAPI.swift` — delete the `#if DEBUG DiffPatchTraceLogger.shared.record(...)` call site (around line 127-129).

- [ ] **Step 1: Delete `JSONPatch.swift`**

```bash
rm "/Users/brian/Dev Me/onDeck/onDeck/Utilities/JSONPatch.swift"
```

- [ ] **Step 2: Delete `DiffPatchTraceLogger.swift`**

```bash
rm "/Users/brian/Dev Me/onDeck/onDeck/Utilities/DiffPatchTraceLogger.swift"
```

- [ ] **Step 3: Remove the `DiffPatchTraceLogger.shared.record(...)` call in `MLBStatsAPI.fetchDiffPatch`**

Search:
```bash
grep -n "DiffPatchTraceLogger" "/Users/brian/Dev Me/onDeck/onDeck/Networking/MLBStatsAPI.swift"
```

Delete the `#if DEBUG ... #endif` block around the `.record(gamePk: gamePk, ops: allPatches)` call.

- [ ] **Step 4: Remove `JSONPatch` from the Xcode project file index**

Xcode's `onDeck.xcodeproj/project.pbxproj` has file references. Deleted files need their entries pulled or Xcode will show them as missing on next open. Grep for references:

```bash
grep -n "JSONPatch\|DiffPatchTraceLogger" "/Users/brian/Dev Me/onDeck/onDeck.xcodeproj/project.pbxproj" | head -20
```

For each match, edit the pbxproj to remove:
1. The `PBXFileReference` line (file declaration)
2. The `PBXBuildFile` line (target membership)
3. Any references in `children = (...)` groups
4. Any references in `files = (...)` build phase lists

If pbxproj surgery gets hairy, open the project in Xcode and delete the files from the navigator (which will clean all four references atomically). Don't break the pbxproj.

- [ ] **Step 5: Build to verify**

```bash
cd "/Users/brian/Dev Me/onDeck" && \
xcodebuild -scheme onDeck -destination 'platform=macOS' -derivedDataPath build build 2>&1 | tail -5
```
Expected: `** BUILD SUCCEEDED **`.

If the build errors because the pbxproj still references a deleted file:
```
error: Build input file cannot be found: '.../JSONPatch.swift'
```
Re-run Step 4 more carefully or open Xcode and clean up via the navigator.

- [ ] **Step 6: Sanity-check — no stale references remain**

```bash
grep -rn "JSONPatch\|DiffPatchTraceLogger\|cachedFeedData\|cachedTimecodes" "/Users/brian/Dev Me/onDeck/onDeck/" "/Users/brian/Dev Me/onDeck/onDeck.xcodeproj/project.pbxproj"
```

Expected: no matches in `.swift` files; pbxproj may have cleaned-up group entries but no file references.

- [ ] **Step 7: Launch one more time and watch /diffPatch traffic**

```bash
pkill -x onDeck 2>/dev/null; \
build/Build/Products/Debug/onDeck.app/Contents/MacOS/onDeck &
sleep 30
```

Open Console.app, filter by `onDeck` process. Watch for:
- `[PatcherTest] all tests passed` at launch
- `[MLB API] GET /diffPatch` entries with `no changes` (normal idle) or `N ops` (state change)
- `[LiveFeedPatcher] unknown: …` entries — expected for decorative paths we deliberately skip. Review for anything that looks like state we *should* handle (if any, add handlers in a follow-up).

Kill: `pkill -x onDeck`.

- [ ] **Step 8: Review `onDeck-unknown-patches.csv` after brief run**

```bash
cat ~/Library/Containers/dev.bjc.onDeck.debug/Data/Library/Caches/onDeck-unknown-patches.csv | head -50
```

Scan for:
- Frequently repeated paths — candidate for a new handler
- Any path that matches a field in `LiveFeedData` that *should* have been registered

Note: this CSV accumulates across runs. First-session volume may be small; a full slate is the real test bed.

- [ ] **Step 9: Commit 2**

```bash
cd "/Users/brian/Dev Me/onDeck"
git add onDeck/Managers/GameMonitor.swift \
        onDeck/App/AppState.swift \
        onDeck/Networking/MLBStatsAPI.swift \
        onDeck/Utilities/JSONPatch.swift \
        onDeck/Utilities/DiffPatchTraceLogger.swift \
        onDeck.xcodeproj/project.pbxproj
git commit -m "switch GameMonitor to typed patcher, delete JSON round-trip pipeline"
```

`git add` on deleted files stages the deletion. If any of those paths errors as nonexistent (rm-d before add), use `git rm --cached` instead (or let `git add -u` catch them). `git status` clean at end.

---

## Post-commit validation (manual, out of plan scope but documented)

These are **run-by-user** steps after both commits land, not plan-executor tasks:

1. Run the app through a full MLB slate day (~4-6 hours of active games).
2. Check Console for any `[LiveFeedPatcher] unknown:` entries that touch modeled state.
3. Review `onDeck-unknown-patches.csv` — sort by frequency, promote anything frequent that matters.
4. Re-enable the memory probe (`memory-probe-2` branch) and compare `phys_footprint` / `malloc_alloc_mb` vs pre-fix baseline:
   - Expected: peak during active slate drops from ~260 MB to ~100-120 MB.
   - Expected: overnight idle drops from ~200 MB to <80 MB.
5. If overnight stays >80 MB, reopen Fix C (`malloc_zone_pressure_relief`) per FIX-DESIGN section C.

---

## Self-review notes

- **Spec coverage**: every section (A-E) of FIX-DESIGN has a task. Fix C is explicitly deferred per spec.
- **Placeholder scan**: no "TBD" / "implement later" / "similar to Task N" in this plan. Full code shown at every step that edits code.
- **Type consistency**: `LiveFeedData`, `PlayerGameStats`, `PlayerBattingStats`, `PlayerPitchingStats`, `LiveFeedPatcher`, `UnknownPatchLogger` — names match across Task 1/2/4/5/6. `invalidateTimecodes()` (not `clearTimecodes`) used consistently in Tasks 6a+6b.
- **Testing infra gap**: called out explicitly at top. The DEBUG self-test pattern is a pragmatic substitute, not a full XCTest setup — sufficient for this fix's scope because the patcher is pure.
