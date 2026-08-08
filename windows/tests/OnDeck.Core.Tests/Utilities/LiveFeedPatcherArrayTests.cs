using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherArrayTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Fact]
    public void BattingOrder_WholeArrayReplace()
    {
        // LiveFeedPatcherTests.swift:153-160
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/battingOrder", "[10, 11, 12]"));
        Assert.Equal([10, 11, 12], patched.HomeBattingOrder);
    }

    [Fact]
    public void WholeArrayReplace_DropsNonNumericEntries()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/away/battingOrder", """[1, "2", null, 3.9, {}]"""));

        Assert.Equal([1, 2, 3], patched.AwayBattingOrder);
    }

    [Fact]
    public void Pitchers_AppendViaDashIndex()
    {
        // LiveFeedPatcherTests.swift:162-169
        var patched = Patch(BaseFeed(), Op("add", "/liveData/boxscore/teams/home/pitchers/-", "9999"));
        Assert.Equal([2, 9999], patched.HomePitchers);
    }

    [Fact]
    public void IndexedReplace_UpdatesInPlace()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/pitchers/0", "77"));
        Assert.Equal([77], patched.HomePitchers);
    }

    [Fact]
    public void IndexedReplace_IsIgnoredWhenOutOfRange()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/boxscore/teams/home/pitchers/5", "77"));
        Assert.Equal([2], patched.HomePitchers);
    }

    [Fact]
    public void IndexedAdd_InsertsAtPosition()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1, 2, 3];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/1", "99"));
        Assert.Equal([1, 99, 2, 3], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedAdd_AppendsWhenIndexEqualsCount()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1, 2];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/2", "3"));
        Assert.Equal([1, 2, 3], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedAdd_AppendsWhenIndexBeyondCount()
    {
        var feed = BaseFeed();
        feed.HomeBattingOrder = [1];

        var patched = Patch(feed, Op("add", "/liveData/boxscore/teams/home/battingOrder/9", "5"));
        Assert.Equal([1, 5], patched.HomeBattingOrder);
    }

    [Fact]
    public void IndexedRemove_DropsTheEntry()
    {
        var feed = BaseFeed();
        feed.AwayPitchers = [1, 2, 3];

        var patched = Patch(feed, Op("remove", "/liveData/boxscore/teams/away/pitchers/1"));
        Assert.Equal([1, 3], patched.AwayPitchers);
    }

    [Fact]
    public void IndexedRemove_IsIgnoredWhenOutOfRange()
    {
        var feed = BaseFeed();
        feed.AwayPitchers = [1];

        var patched = Patch(feed, Op("remove", "/liveData/boxscore/teams/away/pitchers/4"));
        Assert.Equal([1], patched.AwayPitchers);
    }

    [Fact]
    public void AllFourArraysAreAddressable()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/boxscore/teams/home/battingOrder", "[1]"),
            Op("replace", "/liveData/boxscore/teams/away/battingOrder", "[2]"),
            Op("replace", "/liveData/boxscore/teams/home/pitchers", "[3]"),
            Op("replace", "/liveData/boxscore/teams/away/pitchers", "[4]"));

        Assert.Equal([1], patched.HomeBattingOrder);
        Assert.Equal([2], patched.AwayBattingOrder);
        Assert.Equal([3], patched.HomePitchers);
        Assert.Equal([4], patched.AwayPitchers);
    }
}
