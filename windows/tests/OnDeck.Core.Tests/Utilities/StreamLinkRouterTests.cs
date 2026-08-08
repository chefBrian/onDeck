using OnDeck.Core.Models;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class StreamLinkRouterTests
{
    private const int GamePk = 776543;

    private static Game GameWith(params Game.Broadcast[] broadcasts) =>
        new(GamePk, "Los Angeles Dodgers", "San Francisco Giants", 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            HomeProbablePitcherId: null, AwayProbablePitcherId: null,
            Broadcasts: broadcasts, HomeLineup: [], AwayLineup: []);

    [Theory]
    [InlineData("Peacock", "https://www.peacocktv.com/sports/mlb")]
    [InlineData("Apple TV", "https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668")]
    [InlineData("Apple TV+", "https://tv.apple.com/us/room/edt.item.62327df1-6e37-4222-86c1-056489e15668")]
    [InlineData("ESPN", "https://www.espn.com/watch/")]
    [InlineData("ESPN2", "https://www.espn.com/watch/")]
    // .NET's Uri normalizes a bare authority to a trailing slash; Swift's URL keeps it
    // verbatim. Same destination once handed to a browser.
    [InlineData("Netflix", "https://www.netflix.com/")]
    [InlineData("TBS", "https://www.tbs.com/mlb-on-tbs")]
    public void Url_RoutesExclusiveCallSignToItsPlatform(string callSign, string expected)
    {
        var game = GameWith(new Game.Broadcast(callSign, IsExclusive: true));
        Assert.Equal(expected, StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvWhenNoBroadcasts()
    {
        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(GameWith()).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvWhenNoExclusiveBroadcast()
    {
        var game = GameWith(
            new Game.Broadcast("SNLA", IsExclusive: false),
            new Game.Broadcast("Peacock", IsExclusive: false));

        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_FallsBackToMlbTvForUnknownExclusiveCallSign()
    {
        var game = GameWith(new Game.Broadcast("Roku", IsExclusive: true));
        Assert.Equal($"https://www.mlb.com/tv/g{GamePk}", StreamLinkRouter.Url(game).ToString());
    }

    [Fact]
    public void Url_UsesTheFirstExclusiveBroadcast()
    {
        var game = GameWith(
            new Game.Broadcast("SNLA", IsExclusive: false),
            new Game.Broadcast("Netflix", IsExclusive: true),
            new Game.Broadcast("Peacock", IsExclusive: true));

        Assert.Equal("https://www.netflix.com/", StreamLinkRouter.Url(game).ToString());
    }
}
