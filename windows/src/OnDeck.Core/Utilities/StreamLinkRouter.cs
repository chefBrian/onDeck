using OnDeck.Core.Models;

namespace OnDeck.Core.Utilities;

/// <summary>Port of <c>Utilities/StreamLinkRouter.swift</c>.</summary>
public static class StreamLinkRouter
{
    /// <summary>Routes a broadcast callSign to the appropriate streaming platform URL.</summary>
    public static Uri Url(Game game)
    {
        var callSign = game.Broadcasts.FirstOrDefault(broadcast => broadcast.IsExclusive)?.CallSign;

        return callSign switch
        {
            "Peacock" => new Uri("https://www.peacocktv.com/sports/mlb"),
            "Apple TV" or "Apple TV+" =>
                new Uri("https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668"),
            "ESPN" or "ESPN2" => new Uri("https://www.espn.com/watch/"),
            "Netflix" => new Uri("https://www.netflix.com"),
            "TBS" => new Uri("https://www.tbs.com/mlb-on-tbs"),
            _ => MlbTvUrl(game.Id),
        };
    }

    private static Uri MlbTvUrl(int gamePk) => new($"https://www.mlb.com/tv/g{gamePk}");
}
