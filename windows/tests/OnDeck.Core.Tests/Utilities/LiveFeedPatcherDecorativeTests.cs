using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherDecorativeTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Theory]
    [InlineData("/liveData/plays/allPlays/99/playEvents/0")]
    [InlineData("/liveData/plays/currentPlay/playEvents/0/details/code")]
    [InlineData("/liveData/plays/currentPlay/matchup/batterHotColdZones")]
    [InlineData("/liveData/plays/currentPlay/runners")]
    [InlineData("/liveData/boxscore/teams/home/teamStats")]
    [InlineData("/liveData/linescore/defense")]
    [InlineData("/liveData/linescore/offense/onDeck")]
    [InlineData("/liveData/linescore/innings")]
    [InlineData("/metaData/gameEvents")]
    [InlineData("/gameData/weather")]
    [InlineData("/gameData/status/statusCode")]
    [InlineData("/gameData/players")]
    public void DecorativePaths_AreSkippedWithoutLogging(string path)
    {
        var logger = new UnknownPatchLogger();
        var feed = BaseFeed();

        var patched = LiveFeedPatcher.Apply([Op("replace", path, "\"x\"")], feed, logger);

        Assert.Equal(feed, patched);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void DecorativePrefix_MatchesExactPathAndChildrenOnly()
    {
        var logger = new UnknownPatchLogger();

        // "/gameData/weather" is decorative; "/gameData/weatherStation" is not a child of it.
        LiveFeedPatcher.Apply([Op("replace", "/gameData/weatherStation", "\"x\"")], BaseFeed(), logger);

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void HandledPathUnderDecorativeSubtreeStillWins()
    {
        // LiveFeedPatcherTests.swift:127-140. /liveData/plays/currentPlay/about is a decorative
        // prefix, but /currentPlay/about/isComplete IS handled; the specific case must win.
        var feed = BaseFeed();
        feed.IsPlayComplete = false;

        var patched = LiveFeedPatcher.Apply(
            [Op("replace", "/liveData/plays/currentPlay/about/isComplete", "true")],
            feed,
            new UnknownPatchLogger());

        Assert.True(patched.IsPlayComplete);
    }

    [Fact]
    public void HandledCountAndResultPathsSurviveTheirDecorativePrefixes()
    {
        var patched = LiveFeedPatcher.Apply(
            [
                Op("replace", "/liveData/plays/currentPlay/count/balls", "2"),
                Op("add", "/liveData/plays/currentPlay/result/event", "\"Single\""),
            ],
            BaseFeed(),
            new UnknownPatchLogger());

        Assert.Equal(2, patched.Balls);
        Assert.Equal("Single", patched.LastPlayEvent);
    }

    [Fact]
    public void UnknownPathIsRecorded()
    {
        var logger = new UnknownPatchLogger();
        LiveFeedPatcher.Apply([Op("replace", "/liveData/somethingNew", "42")], BaseFeed(), logger);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("replace", entry.Op);
        Assert.Equal("/liveData/somethingNew", entry.Path);
        Assert.Equal("42", entry.ValuePreview);
    }

    [Fact]
    public void UnmodeledPlayerSubtreeIsNotHandled()
    {
        var logger = new UnknownPatchLogger();
        LiveFeedPatcher.Apply(
            [Op("replace", "/liveData/boxscore/teams/away/players/ID1/person/fullName", "\"x\"")],
            BaseFeed(),
            logger);

        Assert.Contains(logger.Entries, e => e.Path.EndsWith("/person/fullName", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownPathIsSkippedSilentlyWithoutALogger()
    {
        var feed = BaseFeed();
        var patched = LiveFeedPatcher.Apply([Op("replace", "/liveData/somethingNew", "42")], feed);

        Assert.Equal(feed, patched);
    }
}
