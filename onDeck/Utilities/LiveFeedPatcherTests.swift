#if DEBUG
import Foundation

/// DEBUG-only self-tests for `LiveFeedPatcher`. Invoked at app launch;
/// failures trigger `preconditionFailure` so broken patcher code cannot ship.
///
/// This is not XCTest - the project has no test target. It's a sequence of
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

        test(&failures, "copy /offense/first from /matchup/batter resolves batter reaches base") {
            // Regression: Luis Robert Jr. singled on 2026-04-18 at 19:06:31 UTC. Server sent:
            //   {"op":"copy","path":"/liveData/linescore/offense/first","from":"/liveData/plays/allPlays/21/matchup/batter"}
            // Patcher had no copy handler → runnerOnFirst stayed nil → UI showed empty diamond.
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.currentBatterID = 673357
            feed.runnerOnFirst = nil
            let p: [[String: Any]] = [
                ["op": "copy",
                 "from": "/liveData/plays/allPlays/21/matchup/batter",
                 "path": "/liveData/linescore/offense/first"]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.runnerOnFirst == 673357)
                ? nil
                : "runnerOnFirst=\(String(describing: feed.runnerOnFirst)) expected 673357"
        }

        test(&failures, "copy /offense/second from /currentPlay/matchup/batter also resolves") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.currentBatterID = 42
            feed.runnerOnSecond = nil
            let p: [[String: Any]] = [
                ["op": "copy",
                 "from": "/liveData/plays/currentPlay/matchup/batter",
                 "path": "/liveData/linescore/offense/second"]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.runnerOnSecond == 42)
                ? nil
                : "runnerOnSecond=\(String(describing: feed.runnerOnSecond)) expected 42"
        }

        test(&failures, "copy to offense base with non-batter from forces reseed via nil timeStamp") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.timeStamp = "20260418_190600"
            let p: [[String: Any]] = [
                ["op": "copy",
                 "from": "/liveData/linescore/offense/second",
                 "path": "/liveData/linescore/offense/first"]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.timeStamp == nil)
                ? nil
                : "timeStamp=\(String(describing: feed.timeStamp)) expected nil for unresolvable copy"
        }

        test(&failures, "add /offense/third with whole-object value sets runnerOnThird from .id") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.runnerOnThird = nil
            let p: [[String: Any]] = [
                ["op": "add",
                 "path": "/liveData/linescore/offense/third",
                 "value": ["id": 805367, "fullName": "Chase Meidroth", "link": "/api/v1/people/805367"]]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.runnerOnThird == 805367)
                ? nil
                : "runnerOnThird=\(String(describing: feed.runnerOnThird)) expected 805367"
        }

        test(&failures, "decorative prefix path under /plays/allPlays is silently skipped") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let before = feed
            let p: [[String: Any]] = [
                ["op": "add", "path": "/liveData/plays/allPlays/99/playEvents/0",
                 "value": ["details": ["description": "Ball"]]]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed == before) ? nil : "decorative prefix allowed state mutation"
        }

        test(&failures, "handled path under a decorative subtree still wins (isComplete under /currentPlay)") {
            // Regression guard: `/liveData/plays/currentPlay` is a decorative-prefix root
            // for many sub-paths we don't model, but `/currentPlay/about/isComplete` IS
            // handled in the switch above. The specific case must win over the prefix.
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            feed.isPlayComplete = false
            let p: [[String: Any]] = [
                ["op": "replace", "path": "/liveData/plays/currentPlay/about/isComplete", "value": true]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed.isPlayComplete == true)
                ? nil
                : "specific handler skipped in favor of decorative prefix"
        }

        test(&failures, "decorative /offense/first/fullName and /link are silent no-ops") {
            var feed = try decodeFeed(json: LiveFeedPatcherFixtures.baseFeedJSON)
            let before = feed
            let p: [[String: Any]] = [
                ["op": "replace", "path": "/liveData/linescore/offense/first/fullName", "value": "Luis Robert Jr."],
                ["op": "replace", "path": "/liveData/linescore/offense/first/link", "value": "/api/v1/people/673357"]
            ]
            LiveFeedPatcher.apply(p, to: &feed)
            return (feed == before) ? nil : "decorative base-slot patch mutated state"
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
