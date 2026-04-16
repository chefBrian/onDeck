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
