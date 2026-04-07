import Foundation

@Observable
final class ScheduleManager {
    var todaysGames: [Game] = []

    func fetchSchedule(for teamIDs: [String]) async {
        // TODO: Fetch today's MLB schedule, filter to relevant teams
    }
}
