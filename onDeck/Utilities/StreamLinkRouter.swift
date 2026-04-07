import Foundation

enum StreamLinkRouter {
    /// Routes a broadcast callSign to the appropriate streaming platform URL.
    static func url(for game: Game) -> URL {
        let exclusiveBroadcast = game.broadcasts.first { $0.isExclusive }

        guard let callSign = exclusiveBroadcast?.callSign else {
            return mlbTVURL(gamePk: game.id)
        }

        switch callSign {
        case "Peacock":
            return URL(string: "https://www.peacocktv.com/sports/mlb")!
        case "Apple TV", "Apple TV+":
            return URL(string: "https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668")!
        case "ESPN", "ESPN2":
            return URL(string: "https://www.espn.com/watch/")!
        case "Netflix":
            return URL(string: "https://www.netflix.com")!
        case "TBS":
            return URL(string: "https://www.tbs.com/mlb-on-tbs")!
        default:
            return mlbTVURL(gamePk: game.id)
        }
    }

    private static func mlbTVURL(gamePk: Int) -> URL {
        URL(string: "https://www.mlb.com/tv/g\(gamePk)")!
    }
}
