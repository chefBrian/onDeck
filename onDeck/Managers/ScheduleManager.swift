import Foundation

@Observable
@MainActor
final class ScheduleManager {
    var todaysGames: [Game] = []
    var error: String?

    private let mlbAPI = MLBStatsAPI()

    /// Fetches today's schedule and filters to games involving the given team names.
    /// Uses "baseball day" - before 8 AM counts as the previous day.
    func fetchSchedule(for teamNames: Set<String>) async {
        error = nil
        do {
            let allGames = try await mlbAPI.fetchSchedule(date: Self.baseballDate())
            todaysGames = allGames.filter { game in
                teamNames.contains(game.homeTeam) || teamNames.contains(game.awayTeam)
            }
        } catch {
            self.error = "Schedule fetch failed: \(error.localizedDescription)"
        }
    }

    /// The "baseball date" - before 8 AM, we're still on yesterday's schedule.
    static func baseballDate() -> Date {
        let calendar = Calendar.current
        let hour = calendar.component(.hour, from: .now)
        if hour < 8 {
            return calendar.date(byAdding: .day, value: -1, to: .now) ?? .now
        }
        return .now
    }
}
