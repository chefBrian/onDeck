using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherRunnerTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    [InlineData("third")]
    public void RunnerId_IsSetByReplaceAndAdd(string slot)
    {
        var replaced = Patch(BaseFeed(), Op("replace", $"/liveData/linescore/offense/{slot}/id", "77"));
        var added = Patch(BaseFeed(), Op("add", $"/liveData/linescore/offense/{slot}/id", "77"));

        Assert.Equal(77, RunnerAt(replaced, slot));
        Assert.Equal(77, RunnerAt(added, slot));
    }

    [Theory]
    [InlineData("first", "/liveData/linescore/offense/first")]
    [InlineData("first", "/liveData/linescore/offense/first/id")]
    [InlineData("second", "/liveData/linescore/offense/second")]
    [InlineData("second", "/liveData/linescore/offense/second/id")]
    [InlineData("third", "/liveData/linescore/offense/third")]
    [InlineData("third", "/liveData/linescore/offense/third/id")]
    public void RunnerId_IsClearedByRemoveOnSlotOrId(string slot, string path)
    {
        var feed = BaseFeed();
        SetRunner(feed, slot, 55);

        Assert.Null(RunnerAt(Patch(feed, Op("remove", path)), slot));
    }

    [Fact]
    public void WholeObjectAdd_SetsRunnerFromIdField()
    {
        // LiveFeedPatcherTests.swift:102-114
        var patched = Patch(BaseFeed(), Op(
            "add",
            "/liveData/linescore/offense/third",
            """{"id": 805367, "fullName": "Chase Meidroth", "link": "/api/v1/people/805367"}"""));

        Assert.Equal(805367, patched.RunnerOnThird);
    }

    [Fact]
    public void WholeObjectAdd_ClearsRunnerWhenValueHasNoId()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 12;

        var patched = Patch(feed, Op("add", "/liveData/linescore/offense/first", """{"fullName": "x"}"""));
        Assert.Null(patched.RunnerOnFirst);
    }

    [Fact]
    public void Copy_FromAllPlaysBatterResolvesBatterReachesBase()
    {
        // Regression: Luis Robert Jr. singled 2026-04-18 19:06:31 UTC; the patcher had no copy
        // handler, so runnerOnFirst stayed nil and the UI showed an empty diamond.
        var feed = BaseFeed();
        feed.CurrentBatterId = 673357;
        feed.RunnerOnFirst = null;

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/first",
            from: "/liveData/plays/allPlays/21/matchup/batter"));

        Assert.Equal(673357, patched.RunnerOnFirst);
    }

    [Fact]
    public void Copy_FromCurrentPlayBatterAlsoResolves()
    {
        var feed = BaseFeed();
        feed.CurrentBatterId = 42;

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/second",
            from: "/liveData/plays/currentPlay/matchup/batter"));

        Assert.Equal(42, patched.RunnerOnSecond);
    }

    [Fact]
    public void Copy_FromNonBatterPathForcesReseedByNullingTimeStamp()
    {
        // LiveFeedPatcherTests.swift:88-100
        var feed = BaseFeed();
        feed.TimeStamp = "20260418_190600";

        var patched = Patch(feed, Op(
            "copy", "/liveData/linescore/offense/first",
            from: "/liveData/linescore/offense/second"));

        Assert.Null(patched.TimeStamp);
    }

    [Fact]
    public void Copy_WithNoFromForcesReseed()
    {
        var feed = BaseFeed();
        feed.TimeStamp = "20260418_190600";

        Assert.Null(Patch(feed, Op("copy", "/liveData/linescore/offense/third")).TimeStamp);
    }

    [Fact]
    public void Move_FirstToSecondTransfersIdAndClearsFirst()
    {
        // LiveFeedPatcherTests.swift:24-34
        var feed = BaseFeed();
        feed.RunnerOnFirst = 99;
        feed.RunnerOnSecond = null;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/second",
            from: "/liveData/linescore/offense/first"));

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnSecond);
    }

    [Fact]
    public void Move_FirstToThirdTransfersIdAndClearsFirst()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 99;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/third",
            from: "/liveData/linescore/offense/first"));

        Assert.Null(patched.RunnerOnFirst);
        Assert.Equal(99, patched.RunnerOnThird);
    }

    [Fact]
    public void Move_SecondToThirdTransfersIdAndClearsSecond()
    {
        var feed = BaseFeed();
        feed.RunnerOnSecond = 88;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/third",
            from: "/liveData/linescore/offense/second"));

        Assert.Null(patched.RunnerOnSecond);
        Assert.Equal(88, patched.RunnerOnThird);
    }

    [Fact]
    public void Move_WithUnrecognisedFromLeavesRunnersUntouched()
    {
        var feed = BaseFeed();
        feed.RunnerOnFirst = 1;
        feed.RunnerOnSecond = 2;

        var patched = Patch(feed, Op(
            "move", "/liveData/linescore/offense/second",
            from: "/liveData/linescore/offense/third"));

        Assert.Equal(1, patched.RunnerOnFirst);
        Assert.Equal(2, patched.RunnerOnSecond);
    }

    [Fact]
    public void DecorativeBaseSlotFields_AreSilentNoOps()
    {
        // LiveFeedPatcherTests.swift:142-151
        var feed = BaseFeed();
        var patched = Patch(
            feed,
            Op("replace", "/liveData/linescore/offense/first/fullName", "\"Luis Robert Jr.\""),
            Op("replace", "/liveData/linescore/offense/first/link", "\"/api/v1/people/673357\""),
            Op("add", "/liveData/linescore/offense/second/fullName", "\"x\""),
            Op("add", "/liveData/linescore/offense/third/link", "\"y\""));

        Assert.Equal(feed, patched);
    }

    private static int? RunnerAt(LiveFeedData feed, string slot) => slot switch
    {
        "first" => feed.RunnerOnFirst,
        "second" => feed.RunnerOnSecond,
        _ => feed.RunnerOnThird,
    };

    private static void SetRunner(LiveFeedData feed, string slot, int id)
    {
        switch (slot)
        {
            case "first": feed.RunnerOnFirst = id; break;
            case "second": feed.RunnerOnSecond = id; break;
            default: feed.RunnerOnThird = id; break;
        }
    }
}
