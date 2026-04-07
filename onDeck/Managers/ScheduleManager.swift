import Foundation

@Observable
@MainActor
final class ScheduleManager {
    var todaysGames: [Game] = []
    var error: String?

    private let mlbAPI = MLBStatsAPI()

    /// Fetches today's schedule and filters to games involving the given team names.
    func fetchSchedule(for teamNames: Set<String>) async {
        error = nil
        do {
            let allGames = try await mlbAPI.fetchSchedule(date: .now)
            todaysGames = allGames.filter { game in
                teamNames.contains(game.homeTeam) || teamNames.contains(game.awayTeam)
            }
        } catch {
            self.error = "Schedule fetch failed: \(error.localizedDescription)"
        }
    }

    /// Re-fetches on day rollover. Call this at midnight or when the app becomes active.
    func refreshIfNeeded(teamNames: Set<String>) async {
        await fetchSchedule(for: teamNames)
    }
}
