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
