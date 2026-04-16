import Foundation

struct MLBStatsAPI: Sendable {

    // MARK: - Player Search

    func searchPlayer(name: String, teamName: String?) async throws -> Int? {
        let cleanName = name.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? name
        let url = URL(string: "https://statsapi.mlb.com/api/v1/people/search?names=\(cleanName)&hydrate=currentTeam")!
        let (data, _) = try await URLSession.shared.data(from: url)
        print("[MLB API] GET /people/search name=\(name) \(Self.formatBytes(data.count))")
        let response = try JSONDecoder().decode(SearchResponse.self, from: data)

        guard let people = response.people, !people.isEmpty else { return nil }

        // If we have a team name for disambiguation, find the matching player
        if let teamName {
            if let match = people.first(where: { person in
                guard let currentTeamName = person.currentTeam?.name else { return false }
                return TeamMapping.matches(mlbTeamName: currentTeamName, fantraxAbbreviation: teamName)
                    || currentTeamName.contains(teamName)
                    || teamName.contains(currentTeamName)
            }) {
                return match.id
            }
        }

        // Fall back to first result
        return people.first?.id
    }

    // MARK: - Schedule

    func fetchSchedule(date: Date) async throws -> [Game] {
        let dateString = Self.dateFormatter.string(from: date)
        let url = URL(string: "https://statsapi.mlb.com/api/v1/schedule?sportId=1&date=\(dateString)&hydrate=team,broadcasts,probablePitcher,lineups")!
        let (data, _) = try await URLSession.shared.data(from: url)
        print("[MLB API] GET /schedule date=\(dateString) \(Self.formatBytes(data.count))")
        let response = try JSONDecoder().decode(ScheduleResponse.self, from: data)

        return response.dates?.flatMap { date in
            date.games.map { game in
                let broadcasts = (game.broadcasts ?? []).compactMap { broadcast -> Game.Broadcast? in
                    guard let callSign = broadcast.callSign else { return nil }
                    let isExclusive = broadcast.availability?.availabilityCode == "exclusive"
                    return Game.Broadcast(callSign: callSign, isExclusive: isExclusive)
                }

                let startTime = Self.iso8601Formatter.date(from: game.gameDate) ?? .now

                return Game(
                    id: game.gamePk,
                    homeTeam: game.teams.home.team.name,
                    awayTeam: game.teams.away.team.name,
                    homeTeamID: game.teams.home.team.id,
                    awayTeamID: game.teams.away.team.id,
                    startTime: startTime,
                    homeProbablePitcherID: game.teams.home.probablePitcher?.id,
                    awayProbablePitcherID: game.teams.away.probablePitcher?.id,
                    broadcasts: broadcasts,
                    homeLineup: game.lineups?.homePlayers?.map(\.id) ?? [],
                    awayLineup: game.lineups?.awayPlayers?.map(\.id) ?? []
                )
            }
        } ?? []
    }

    // MARK: - Live Feed

    /// Fetches the full live feed and returns parsed data + raw bytes for caching.
    func fetchLiveFeedRaw(gamePk: Int, label: String? = nil) async throws -> (feed: LiveFeedData, rawData: Data, timecode: String?) {
        let url = URL(string: "https://statsapi.mlb.com/api/v1.1/game/\(gamePk)/feed/live")!
        let (data, _) = try await URLSession.shared.data(from: url)
        let tag = label.map { " \($0)" } ?? ""
        print("[MLB API] GET /feed/live game=\(gamePk)\(tag) \(Self.formatBytes(data.count))")
        let feed = try Self.decodeLiveFeed(from: data)
        return (feed, data, feed.timeStamp)
    }

    /// Decodes a LiveFeedData from raw JSON bytes (used after patching cached data).
    static func decodeLiveFeed(from data: Data) throws -> LiveFeedData {
        let response = try JSONDecoder().decode(LiveFeedResponse.self, from: data)
        return Self.parseLiveFeedResponse(response)
    }

    // MARK: - Diff Patch

    /// Fetches diff patches for a game since a given timecode.
    func fetchDiffPatch(gamePk: Int, since timecode: String, label: String? = nil) async throws -> DiffPatchResult {
        let now = Self.currentTimecode()
        let url = URL(string: "https://statsapi.mlb.com/api/v1.1/game/\(gamePk)/feed/live/diffPatch?startTimecode=\(timecode)&endTimecode=\(now)")!
        let (data, _) = try await URLSession.shared.data(from: url)
        let tag = label.map { " \($0)" } ?? ""

        let parsed = try JSONSerialization.jsonObject(with: data)

        // API sometimes returns a single feed object (dict) instead of an array
        if parsed is [String: Any] {
            print("[MLB API] GET /diffPatch game=\(gamePk)\(tag) \(Self.formatBytes(data.count)) full update")
            return .fullUpdate(data)
        }

        guard let array = parsed as? [[String: Any]] else {
            print("[MLB API] GET /diffPatch game=\(gamePk)\(tag) \(Self.formatBytes(data.count)) (unparseable)")
            return .fullUpdate(data)
        }

        if array.isEmpty {
            print("[MLB API] GET /diffPatch game=\(gamePk)\(tag) \(Self.formatBytes(data.count)) no changes")
            return .noChanges
        }

        // Check if entries have "diff" keys (patches) or are full feed objects (fallback)
        var allPatches: [[String: Any]] = []
        for entry in array {
            if let diff = entry["diff"] as? [[String: Any]] {
                allPatches.append(contentsOf: diff)
            } else {
                // API returned a full feed object instead of patches - serialize it back
                let entryData = try JSONSerialization.data(withJSONObject: entry)
                print("[MLB API] GET /diffPatch game=\(gamePk)\(tag) \(Self.formatBytes(data.count)) full update")
                return .fullUpdate(entryData)
            }
        }

        print("[MLB API] GET /diffPatch game=\(gamePk)\(tag) \(Self.formatBytes(data.count)) \(allPatches.count) ops")
#if DEBUG
        DiffPatchTraceLogger.shared.record(gamePk: gamePk, ops: allPatches)
#endif
        return .patches(allPatches)
    }

    private static func currentTimecode() -> String {
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd_HHmmss"
        formatter.timeZone = TimeZone(identifier: "UTC")
        return formatter.string(from: Date.now)
    }

    // MARK: - Live Feed Parsing

    private static func parseLiveFeedResponse(_ response: LiveFeedResponse) -> LiveFeedData {
        let currentPlay = response.liveData.plays?.currentPlay
        let linescore = response.liveData.linescore
        let offense = linescore?.offense
        let boxscore = response.liveData.boxscore
        let playerStats = parsePlayerStats(boxscore: boxscore)

        return LiveFeedData(
            timeStamp: response.metaData?.timeStamp,
            gameState: response.gameData.status.abstractGameState,
            detailedState: response.gameData.status.detailedState,
            currentBatterID: currentPlay?.matchup.batter.id,
            currentBatterName: currentPlay?.matchup.batter.fullName,
            currentPitcherID: currentPlay?.matchup.pitcher.id,
            currentPitcherName: currentPlay?.matchup.pitcher.fullName,
            inning: linescore?.currentInning,
            inningHalf: linescore?.inningHalf,
            inningState: linescore?.inningState,
            homeScore: linescore?.teams?.home.runs ?? 0,
            awayScore: linescore?.teams?.away.runs ?? 0,
            homeTeam: response.gameData.teams.home.name,
            awayTeam: response.gameData.teams.away.name,
            homeTeamID: response.gameData.teams.home.id,
            awayTeamID: response.gameData.teams.away.id,
            balls: currentPlay?.count?.balls ?? 0,
            strikes: currentPlay?.count?.strikes ?? 0,
            outs: currentPlay?.count?.outs ?? 0,
            runnerOnFirst: offense?.first?.id,
            runnerOnSecond: offense?.second?.id,
            runnerOnThird: offense?.third?.id,
            isPlayComplete: currentPlay?.about.isComplete ?? false,
            lastPlayEvent: currentPlay?.result?.event,
            lastPlayDescription: currentPlay?.result?.description,
            homeBattingOrder: boxscore?.teams.home.battingOrder ?? [],
            awayBattingOrder: boxscore?.teams.away.battingOrder ?? [],
            homePitchers: boxscore?.teams.home.pitchers ?? [],
            awayPitchers: boxscore?.teams.away.pitchers ?? [],
            playerStats: playerStats
        )
    }

    // MARK: - Game Changes

    /// Returns the set of gamePks that have been updated since `since`.
    func fetchGameChanges(since: Date) async throws -> Set<Int> {
        let timestamp = Self.iso8601Formatter.string(from: since)
        let encoded = timestamp.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed) ?? timestamp
        let url = URL(string: "https://statsapi.mlb.com/api/v1/game/changes?updatedSince=\(encoded)&sportId=1")!
        let (data, _) = try await URLSession.shared.data(from: url)
        print("[MLB API] GET /game/changes \(Self.formatBytes(data.count))")
        let response = try JSONDecoder().decode(GameChangesResponse.self, from: data)
        let gamePks = response.dates?.flatMap { $0.games.map(\.gamePk) } ?? []
        return Set(gamePks)
    }

    // MARK: - Player Stats Parsing

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

    // MARK: - Formatting

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f
    }()

    private static let iso8601Formatter = ISO8601DateFormatter()

    private static func formatBytes(_ count: Int) -> String {
        if count < 1024 { return "\(count)B" }
        return String(format: "%.1fKB", Double(count) / 1024)
    }
}

/// Result of a diffPatch request.
enum DiffPatchResult {
    case noChanges
    case patches([[String: Any]])
    case fullUpdate(Data) // API returned full feed instead of patches
}

// MARK: - Public Live Feed Model

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

struct PlayerGameStats: Sendable, Equatable, Codable {
    var batting: PlayerBattingStats? = nil
    var pitching: PlayerPitchingStats? = nil
}

struct PlayerBattingStats: Sendable, Equatable, Codable {
    var atBats: Int? = nil
    var hits: Int? = nil
    var runs: Int? = nil
    var doubles: Int? = nil
    var triples: Int? = nil
    var homeRuns: Int? = nil
    var rbi: Int? = nil
    var baseOnBalls: Int? = nil
    var strikeOuts: Int? = nil
    var stolenBases: Int? = nil

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
    var inningsPitched: String? = nil
    var hits: Int? = nil
    var earnedRuns: Int? = nil
    var strikeOuts: Int? = nil
    var baseOnBalls: Int? = nil
    var numberOfPitches: Int? = nil

    var formatted: String? {
        guard let ip = inningsPitched, ip != "0.0" else { return nil }
        var parts = ["\(ip) IP"]
        if let k = strikeOuts, k > 0 { parts.append("\(k)K") }
        if let er = earnedRuns { parts.append("\(er)ER") }
        if let np = numberOfPitches, np > 0 { parts.append("\(np)P") }
        return parts.joined(separator: ", ")
    }
}

// MARK: - Private Codable Types (JSON Parsing Only)

private struct SearchResponse: Codable {
    let people: [SearchPerson]?
}

private struct SearchPerson: Codable {
    let id: Int
    let fullName: String
    let currentTeam: SearchTeam?
}

private struct SearchTeam: Codable {
    let id: Int
    let name: String
}

private struct ScheduleResponse: Codable {
    let dates: [ScheduleDate]?
}

private struct ScheduleDate: Codable {
    let games: [ScheduleGame]
}

private struct ScheduleGame: Codable {
    let gamePk: Int
    let gameDate: String
    let status: ScheduleGameStatus
    let teams: ScheduleGameTeams
    let broadcasts: [ScheduleBroadcast]?
    let lineups: ScheduleLineups?
}

private struct ScheduleLineups: Codable {
    let homePlayers: [ScheduleLineupPlayer]?
    let awayPlayers: [ScheduleLineupPlayer]?
}

private struct ScheduleLineupPlayer: Codable {
    let id: Int
}

private struct ScheduleGameStatus: Codable {
    let abstractGameState: String
    let detailedState: String?
}

private struct ScheduleGameTeams: Codable {
    let away: ScheduleTeamEntry
    let home: ScheduleTeamEntry
}

private struct ScheduleTeamEntry: Codable {
    let team: ScheduleTeamInfo
    let probablePitcher: ScheduleProbablePitcher?
}

private struct ScheduleProbablePitcher: Codable {
    let id: Int
}

private struct ScheduleTeamInfo: Codable {
    let id: Int
    let name: String
}

private struct ScheduleBroadcast: Codable {
    let type: String?
    let callSign: String?
    let availability: BroadcastAvailability?
}

private struct BroadcastAvailability: Codable {
    let availabilityCode: String?
}

private struct LiveFeedResponse: Codable {
    let metaData: FeedMetaData?
    let gameData: FeedGameData
    let liveData: FeedLiveData
}

private struct FeedMetaData: Codable {
    let timeStamp: String?
}

private struct FeedGameData: Codable {
    let status: FeedGameStatus
    let teams: FeedGameTeams
}

private struct FeedGameStatus: Codable {
    let abstractGameState: String
    let detailedState: String?
}

private struct FeedGameTeams: Codable {
    let away: FeedTeamEntry
    let home: FeedTeamEntry
}

private struct FeedTeamEntry: Codable {
    let id: Int
    let name: String
}

private struct FeedLiveData: Codable {
    let plays: FeedPlays?
    let linescore: FeedLinescore?
    let boxscore: FeedBoxscore?
}

private struct FeedBoxscore: Codable {
    let teams: FeedBoxscoreTeams
}

private struct FeedBoxscoreTeams: Codable {
    let away: FeedBoxscoreTeamEntry
    let home: FeedBoxscoreTeamEntry
}

private struct FeedBoxscoreTeamEntry: Codable {
    let battingOrder: [Int]?
    let pitchers: [Int]?
    let players: [String: FeedBoxscorePlayer]?
}

private struct FeedBoxscorePlayer: Codable {
    let stats: FeedBoxscorePlayerStats?
}

private struct FeedBoxscorePlayerStats: Codable {
    let batting: PlayerBattingStats?
    let pitching: PlayerPitchingStats?
}

private struct FeedPlays: Codable {
    let currentPlay: FeedCurrentPlay?
}

private struct FeedCurrentPlay: Codable {
    let result: FeedPlayResult?
    let about: FeedPlayAbout
    let matchup: FeedMatchup
    let count: FeedPlayCount?
}

private struct FeedPlayResult: Codable {
    let type: String?
    let event: String?
    let description: String?
}

private struct FeedPlayAbout: Codable {
    let isComplete: Bool
}

private struct FeedMatchup: Codable {
    let batter: FeedPlayer
    let pitcher: FeedPlayer
}

private struct FeedPlayer: Codable {
    let id: Int
    let fullName: String
}

private struct FeedPlayCount: Codable {
    let balls: Int
    let strikes: Int
    let outs: Int
}

private struct FeedLinescore: Codable {
    let currentInning: Int?
    let inningHalf: String?
    let inningState: String?
    let teams: FeedLinescoreTeams?
    let offense: FeedOffense?
}

private struct FeedOffense: Codable {
    let first: FeedRunner?
    let second: FeedRunner?
    let third: FeedRunner?
}

private struct FeedRunner: Codable {
    let id: Int?
    let fullName: String?
}

private struct FeedLinescoreTeams: Codable {
    let home: FeedLinescoreTeam
    let away: FeedLinescoreTeam
}

private struct FeedLinescoreTeam: Codable {
    let runs: Int?
}

private struct GameChangesResponse: Codable {
    let dates: [GameChangesDate]?
}

private struct GameChangesDate: Codable {
    let games: [GameChangesGame]
}

private struct GameChangesGame: Codable {
    let gamePk: Int
}
