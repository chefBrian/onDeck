using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;
using OnDeck.Core.Utilities;
using static OnDeck.Core.Tests.Utilities.LiveFeedPatcherScalarTests;

namespace OnDeck.Core.Tests.Utilities;

public class LiveFeedPatcherStatsTests
{
    private static LiveFeedData BaseFeed() => LiveFeedDecoder.Decode(LiveFeedPatcherFixtures.BaseFeedJson);

    [Fact]
    public void FullPlayerAdd_StoresDecodedStats()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID500",
            """{"person": {"id": 500}, "stats": {"batting": {"atBats": 3, "hits": 2}}}"""));

        Assert.Equal(3, patched.PlayerStats[500].Batting!.AtBats);
        Assert.Equal(2, patched.PlayerStats[500].Batting!.Hits);
        Assert.Null(patched.PlayerStats[500].Pitching);
    }

    [Fact]
    public void FullPlayerAdd_StoresNothingWhenStatsHaveNoBattingOrPitching()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID501", """{"stats": {}}"""));

        Assert.False(patched.PlayerStats.ContainsKey(501));
    }

    [Fact]
    public void FullPlayerRemove_DropsTheEntry()
    {
        var patched = Patch(BaseFeed(), Op("remove", "/liveData/boxscore/teams/away/players/ID1"));
        Assert.False(patched.PlayerStats.ContainsKey(1));
    }

    [Fact]
    public void BattingSubtree_ReplacesTheWholeHalf()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/away/players/ID1/stats/batting",
            """{"atBats": 4, "hits": 3, "rbi": 1}"""));

        var batting = patched.PlayerStats[1].Batting!;
        Assert.Equal(4, batting.AtBats);
        Assert.Equal(3, batting.Hits);
        Assert.Equal(1, batting.Rbi);
    }

    [Fact]
    public void BattingSubtree_RemoveClearsIt()
    {
        var patched = Patch(BaseFeed(), Op("remove", "/liveData/boxscore/teams/away/players/ID1/stats/batting"));
        Assert.Null(patched.PlayerStats[1].Batting);
    }

    [Fact]
    public void PitchingSubtree_ReplacesTheWholeHalf()
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching",
            """{"inningsPitched": "5.2", "strikeOuts": 8}"""));

        Assert.Equal("5.2", patched.PlayerStats[2].Pitching!.InningsPitched);
        Assert.Equal(8, patched.PlayerStats[2].Pitching!.StrikeOuts);
    }

    [Fact]
    public void StatsSubtree_CreatesEntryForUnknownPlayer()
    {
        var patched = Patch(BaseFeed(), Op(
            "add", "/liveData/boxscore/teams/home/players/ID777/stats/batting", """{"atBats": 1}"""));

        Assert.Equal(1, patched.PlayerStats[777].Batting!.AtBats);
    }

    [Theory]
    [InlineData("atBats")]
    [InlineData("hits")]
    [InlineData("runs")]
    [InlineData("doubles")]
    [InlineData("triples")]
    [InlineData("homeRuns")]
    [InlineData("rbi")]
    [InlineData("baseOnBalls")]
    [InlineData("strikeOuts")]
    [InlineData("stolenBases")]
    public void BattingField_IsSetIndividually(string field)
    {
        var patched = Patch(BaseFeed(), Op(
            "replace", $"/liveData/boxscore/teams/away/players/ID1/stats/batting/{field}", "6"));

        var batting = patched.PlayerStats[1].Batting!;
        var actual = field switch
        {
            "atBats" => batting.AtBats,
            "hits" => batting.Hits,
            "runs" => batting.Runs,
            "doubles" => batting.Doubles,
            "triples" => batting.Triples,
            "homeRuns" => batting.HomeRuns,
            "rbi" => batting.Rbi,
            "baseOnBalls" => batting.BaseOnBalls,
            "strikeOuts" => batting.StrikeOuts,
            _ => batting.StolenBases,
        };
        Assert.Equal(6, actual);
    }

    [Fact]
    public void BattingField_RemoveNullsIt()
    {
        var patched = Patch(BaseFeed(), Op(
            "remove", "/liveData/boxscore/teams/away/players/ID1/stats/batting/atBats"));

        Assert.Null(patched.PlayerStats[1].Batting!.AtBats);
    }

    [Fact]
    public void BattingField_UnknownNameIsIgnoredButHandled()
    {
        var feed = BaseFeed();
        var patched = Patch(feed, Op(
            "replace", "/liveData/boxscore/teams/away/players/ID1/stats/batting/leftOnBase", "4"));

        Assert.Equal(feed.PlayerStats[1].Batting, patched.PlayerStats[1].Batting);
    }

    [Fact]
    public void PitchingFields_AreSetIndividually()
    {
        var patched = Patch(
            BaseFeed(),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/inningsPitched", "\"6.1\""),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/hits", "4"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/earnedRuns", "2"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/strikeOuts", "7"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/baseOnBalls", "1"),
            Op("replace", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/numberOfPitches", "98"));

        var pitching = patched.PlayerStats[2].Pitching!;
        Assert.Equal("6.1", pitching.InningsPitched);
        Assert.Equal(4, pitching.Hits);
        Assert.Equal(2, pitching.EarnedRuns);
        Assert.Equal(7, pitching.StrikeOuts);
        Assert.Equal(1, pitching.BaseOnBalls);
        Assert.Equal(98, pitching.NumberOfPitches);
    }

    [Fact]
    public void PitchingField_RemoveNullsIt()
    {
        var patched = Patch(BaseFeed(), Op(
            "remove", "/liveData/boxscore/teams/home/players/ID2/stats/pitching/inningsPitched"));

        Assert.Null(patched.PlayerStats[2].Pitching!.InningsPitched);
    }

    [Fact]
    public void ZeroInitCopyIntoModeledStatFieldIsSkippedSafely()
    {
        // LiveFeedPatcherTests.swift:43-53. A copy op carries no value, so the field is nulled
        // rather than zeroed — and since ID1's hits was already null, the feed is unchanged.
        var feed = BaseFeed();
        var patched = Patch(feed, Op(
            "copy", "/liveData/boxscore/teams/away/players/ID1/stats/batting/hits",
            from: "/liveData/plays/currentPlay/result/rbi"));

        Assert.Equal(feed, patched);
    }
}
