using System.Text.Json;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherScalarTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    internal static PatchOperation Op(string op, string path, string? valueJson = null, string? from = null)
    {
        var value = valueJson is null
            ? (JsonElement?)null
            : JsonDocument.Parse(valueJson).RootElement.Clone();
        return new PatchOperation(op, path, value, from);
    }

    internal static LiveFeedData Patch(LiveFeedData feed, params PatchOperation[] ops) =>
        LiveFeedPatcher.Apply(ops, feed);

    [Fact]
    public void Apply_DoesNotMutateTheInputFeed()
    {
        var feed = BaseFeed();
        var patched = Patch(feed, Op("replace", "/metaData/timeStamp", "\"20260416_180010\""));

        Assert.Equal("20260416_180000", feed.TimeStamp);
        Assert.Equal("20260416_180010", patched.TimeStamp);
        Assert.NotSame(feed, patched);
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("add")]
    public void TimeStamp_IsSetByReplaceAndAdd(string op)
    {
        Assert.Equal("t", Patch(BaseFeed(), Op(op, "/metaData/timeStamp", "\"t\"")).TimeStamp);
    }

    [Fact]
    public void AbstractGameState_KeepsPreviousValueWhenPayloadIsNotAString()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/status/abstractGameState", "5"));
        Assert.Equal("Live", patched.GameState);
    }

    [Fact]
    public void AbstractGameState_IsReplacedByStringPayload()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/status/abstractGameState", "\"Final\""));
        Assert.Equal("Final", patched.GameState);
    }

    [Fact]
    public void DetailedState_IsClearedByRemove()
    {
        Assert.Null(Patch(BaseFeed(), Op("remove", "/gameData/status/detailedState")).DetailedState);
    }

    [Fact]
    public void DetailedState_IsNulledByNonStringPayload()
    {
        Assert.Null(Patch(BaseFeed(), Op("replace", "/gameData/status/detailedState", "7")).DetailedState);
    }

    [Fact]
    public void TeamNamesAndIds_AreReplaced()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/gameData/teams/home/name", "\"New Home\""),
            Op("replace", "/gameData/teams/home/id", "999"),
            Op("replace", "/gameData/teams/away/name", "\"New Away\""),
            Op("replace", "/gameData/teams/away/id", "888"));

        Assert.Equal("New Home", patched.HomeTeam);
        Assert.Equal(999, patched.HomeTeamId);
        Assert.Equal("New Away", patched.AwayTeam);
        Assert.Equal(888, patched.AwayTeamId);
    }

    [Fact]
    public void TeamIds_KeepPreviousValueWhenPayloadIsNotNumeric()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/gameData/teams/home/id", "true"));
        Assert.Equal(222, patched.HomeTeamId);
    }

    [Fact]
    public void Matchup_UpdatesBatterAndPitcher()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/plays/currentPlay/matchup/batter/id", "10"),
            Op("replace", "/liveData/plays/currentPlay/matchup/batter/fullName", "\"New Batter\""),
            Op("add", "/liveData/plays/currentPlay/matchup/pitcher/id", "20"),
            Op("add", "/liveData/plays/currentPlay/matchup/pitcher/fullName", "\"New Pitcher\""));

        Assert.Equal(10, patched.CurrentBatterId);
        Assert.Equal("New Batter", patched.CurrentBatterName);
        Assert.Equal(20, patched.CurrentPitcherId);
        Assert.Equal("New Pitcher", patched.CurrentPitcherName);
    }

    [Fact]
    public void IsComplete_KeepsPreviousValueWhenPayloadIsNotBoolean()
    {
        var feed = BaseFeed();
        feed.IsPlayComplete = true;

        var patched = Patch(feed, Op("replace", "/liveData/plays/currentPlay/about/isComplete", "\"yes\""));
        Assert.True(patched.IsPlayComplete);
    }

    [Fact]
    public void IsComplete_IsReplacedByBooleanPayload()
    {
        var patched = Patch(BaseFeed(), Op("replace", "/liveData/plays/currentPlay/about/isComplete", "true"));
        Assert.True(patched.IsPlayComplete);
    }

    [Fact]
    public void PlayResult_IsSetAndCleared()
    {
        var withResult = Patch(
            BaseFeed(),
            Op("add", "/liveData/plays/currentPlay/result/event", "\"Home Run\""),
            Op("add", "/liveData/plays/currentPlay/result/description", "\"blast\""));

        Assert.Equal("Home Run", withResult.LastPlayEvent);
        Assert.Equal("blast", withResult.LastPlayDescription);

        var cleared = Patch(
            withResult,
            Op("remove", "/liveData/plays/currentPlay/result/event"),
            Op("remove", "/liveData/plays/currentPlay/result/description"));

        Assert.Null(cleared.LastPlayEvent);
        Assert.Null(cleared.LastPlayDescription);
    }

    [Fact]
    public void Count_IsUpdatedFromCurrentPlay()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/plays/currentPlay/count/balls", "3"),
            Op("replace", "/liveData/plays/currentPlay/count/strikes", "2"),
            Op("replace", "/liveData/plays/currentPlay/count/outs", "1"));

        Assert.Equal(3, patched.Balls);
        Assert.Equal(2, patched.Strikes);
        Assert.Equal(1, patched.Outs);
    }

    [Fact]
    public void Count_IsAlsoUpdatedFromLinescoreMirrors()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/balls", "1"),
            Op("replace", "/liveData/linescore/strikes", "1"),
            Op("replace", "/liveData/linescore/outs", "2"));

        Assert.Equal(1, patched.Balls);
        Assert.Equal(1, patched.Strikes);
        Assert.Equal(2, patched.Outs);
    }

    [Fact]
    public void Count_KeepsPreviousValueWhenPayloadIsNotNumeric()
    {
        var feed = BaseFeed();
        feed.Balls = 2;

        Assert.Equal(2, Patch(feed, Op("replace", "/liveData/plays/currentPlay/count/balls", "null")).Balls);
    }

    [Fact]
    public void Linescore_UpdatesInningAndScores()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "7"),
            Op("replace", "/liveData/linescore/inningHalf", "\"Bottom\""),
            Op("replace", "/liveData/linescore/inningState", "\"Middle\""),
            Op("replace", "/liveData/linescore/teams/home/runs", "4"),
            Op("replace", "/liveData/linescore/teams/away/runs", "5"));

        Assert.Equal(7, patched.Inning);
        Assert.Equal("Bottom", patched.InningHalf);
        Assert.Equal("Middle", patched.InningState);
        Assert.Equal(4, patched.HomeScore);
        Assert.Equal(5, patched.AwayScore);
    }

    [Fact]
    public void Scores_KeepPreviousValueWhenPayloadIsNotNumeric()
    {
        var feed = BaseFeed();
        feed.HomeScore = 3;

        Assert.Equal(3, Patch(feed, Op("replace", "/liveData/linescore/teams/home/runs", "\"x\"")).HomeScore);
    }

    [Fact]
    public void IntValue_AcceptsNumbersTruncatedDoublesAndNumericStrings()
    {
        Assert.Equal(9, Patch(BaseFeed(), Op("replace", "/liveData/linescore/currentInning", "\"9\"")).Inning);
        Assert.Equal(9, Patch(BaseFeed(), Op("replace", "/liveData/linescore/currentInning", "9.7")).Inning);
    }

    [Fact]
    public void Apply_ProcessesOpsInOrder()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/linescore/currentInning", "3"),
            Op("replace", "/liveData/linescore/currentInning", "4"));

        Assert.Equal(4, patched.Inning);
    }
}
