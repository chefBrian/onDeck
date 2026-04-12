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

    func syncRoster(leagueID: String, teamID: String) async {
        isSyncing = true
        error = nil

        do {
            let fantraxPlayers = try await fantraxAPI.fetchRoster(
                leagueID: leagueID,
                teamID: teamID
            )

            // Resolve all MLB IDs in parallel.
            // Capture the API as a value so child tasks don't retain `self`.
            let api = mlbAPI
            let resolved = await withTaskGroup(of: (FantraxAPI.FantraxPlayer, Int)?.self) { group in
                for fp in fantraxPlayers {
                    group.addTask {
                        let cleanedName = NameCleaner.clean(fp.name)
                        guard let mlbID = try? await api.searchPlayer(
                            name: cleanedName,
                            teamName: fp.teamShortName
                        ) else { return nil }
                        return (fp, mlbID)
                    }
                }
                var results: [(FantraxAPI.FantraxPlayer, Int)] = []
                for await result in group {
                    if let result { results.append(result) }
                }
                return results
            }

            var resolvedPlayers: [Int: Player] = [:] // keyed by MLB ID for dedup

            for (fp, mlbID) in resolved {
                let cleanedName = NameCleaner.clean(fp.name)
                let positions = Self.parsePositions(fp.positions)
                let rawPositions = Set(fp.positions.map { $0.trimmingCharacters(in: .whitespaces).uppercased() })
                let rosterStatus = Player.RosterStatus(rawValue: fp.statusId) ?? .active

                if let existing = resolvedPlayers[mlbID] {
                    // Merge positions for two-way players (e.g., Ohtani)
                    let merged = existing.positions.union(positions)
                    let mergedRaw = existing.fantraxPositions.union(rawPositions)
                    // Use the most active status when merging
                    let bestStatus = existing.rosterStatus.rawValue < rosterStatus.rawValue
                        ? existing.rosterStatus : rosterStatus
                    resolvedPlayers[mlbID] = Player(
                        id: mlbID,
                        name: existing.name,
                        team: existing.team,
                        positions: merged,
                        fantraxPositions: mergedRaw,
                        rosterStatus: bestStatus
                    )
                } else {
                    let teamName = TeamMapping.mlbTeamName(for: fp.teamShortName) ?? fp.teamShortName
                    resolvedPlayers[mlbID] = Player(
                        id: mlbID,
                        name: cleanedName,
                        team: teamName,
                        positions: positions,
                        fantraxPositions: rawPositions,
                        rosterStatus: rosterStatus
                    )
                }
            }

            players = Array(resolvedPlayers.values).sorted { $0.name < $1.name }
            lastSyncDate = Date()
            cacheRoster()
            await HeadshotCache.shared.prefetch(playerIDs: players.map(\.id))
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
        let cached = players.map {
            CachedPlayer(id: $0.id, name: $0.name, team: $0.team, positions: $0.positions, fantraxPositions: $0.fantraxPositions, rosterStatus: $0.rosterStatus)
        }
        if let data = try? JSONEncoder().encode(cached) {
            UserDefaults.standard.set(data, forKey: Self.cacheKey)
        }
    }

    private func loadCachedRoster() {
        guard let data = UserDefaults.standard.data(forKey: Self.cacheKey),
              let cached = try? JSONDecoder().decode([CachedPlayer].self, from: data) else { return }
        players = cached.map {
            Player(id: $0.id, name: $0.name, team: $0.team, positions: $0.positions, fantraxPositions: $0.fantraxPositions ?? [], rosterStatus: $0.rosterStatus ?? .active)
        }
        Task { await HeadshotCache.shared.prefetch(playerIDs: players.map(\.id)) }
    }

    private struct CachedPlayer: Codable {
        let id: Int
        let name: String
        let team: String
        let positions: Set<Player.PlayerPosition>
        let fantraxPositions: Set<String>?
        let rosterStatus: Player.RosterStatus?
    }
}
