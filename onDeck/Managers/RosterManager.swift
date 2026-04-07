import Foundation

@Observable
@MainActor
final class RosterManager {
    var players: [Player] = []
    var lastSyncDate: Date?
    var error: String?
    var isSyncing = false

    private let fantraxAPI = FantraxAPI()
    private let mlbAPI = MLBStatsAPI()

    private static let cacheKey = "cachedRoster"

    init() {
        loadCachedRoster()
    }

    func syncRoster(from url: String) async {
        guard let parsed = FantraxURLParser.parse(url) else {
            error = "Invalid Fantrax URL. Expected format: fantrax.com/fantasy/league/.../team/roster;teamId=..."
            return
        }

        isSyncing = true
        error = nil

        do {
            let fantraxPlayers = try await fantraxAPI.fetchRoster(
                leagueID: parsed.leagueID,
                teamID: parsed.teamID
            )

            var resolvedPlayers: [Int: Player] = [:] // keyed by MLB ID for dedup

            for fp in fantraxPlayers {
                let cleanedName = NameCleaner.clean(fp.name)
                let teamAbbr = fp.teamShortName

                guard let mlbID = try await mlbAPI.searchPlayer(
                    name: cleanedName,
                    teamName: teamAbbr
                ) else {
                    continue // Skip players we can't resolve
                }

                let positions = Self.parsePositions(fp.positions)

                if var existing = resolvedPlayers[mlbID] {
                    // Merge positions for two-way players (e.g., Ohtani)
                    let merged = existing.positions.union(positions)
                    resolvedPlayers[mlbID] = Player(
                        id: mlbID,
                        name: existing.name,
                        team: existing.team,
                        positions: merged
                    )
                } else {
                    let teamName = TeamMapping.mlbTeamName(for: teamAbbr) ?? teamAbbr
                    resolvedPlayers[mlbID] = Player(
                        id: mlbID,
                        name: cleanedName,
                        team: teamName,
                        positions: positions
                    )
                }
            }

            players = Array(resolvedPlayers.values).sorted { $0.name < $1.name }
            lastSyncDate = Date()
            cacheRoster()
        } catch {
            self.error = "Roster sync failed: \(error.localizedDescription)"
            // Keep last cached roster if available
        }

        isSyncing = false
    }

    /// Determines pitcher vs hitter from Fantrax position strings.
    /// SP, RP, P = pitcher. Everything else = hitter.
    private static func parsePositions(_ positions: [String]) -> Set<Player.PlayerPosition> {
        let pitcherCodes: Set<String> = ["SP", "RP", "P"]
        var result = Set<Player.PlayerPosition>()
        for pos in positions {
            let trimmed = pos.trimmingCharacters(in: .whitespaces).uppercased()
            if pitcherCodes.contains(trimmed) {
                result.insert(.pitcher)
            } else {
                result.insert(.hitter)
            }
        }
        if result.isEmpty {
            result.insert(.hitter) // default to hitter
        }
        return result
    }

    // MARK: - Caching

    private func cacheRoster() {
        let cached = players.map { CachedPlayer(id: $0.id, name: $0.name, team: $0.team, positions: $0.positions) }
        if let data = try? JSONEncoder().encode(cached) {
            UserDefaults.standard.set(data, forKey: Self.cacheKey)
        }
    }

    private func loadCachedRoster() {
        guard let data = UserDefaults.standard.data(forKey: Self.cacheKey),
              let cached = try? JSONDecoder().decode([CachedPlayer].self, from: data) else { return }
        players = cached.map { Player(id: $0.id, name: $0.name, team: $0.team, positions: $0.positions) }
    }

    private struct CachedPlayer: Codable {
        let id: Int
        let name: String
        let team: String
        let positions: Set<Player.PlayerPosition>
    }
}
