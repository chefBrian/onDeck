using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class LiveFeedDataTests
{
    private static LiveFeedData Sample() => new()
    {
        TimeStamp = "20260416_180000",
        GameState = "Live",
        DetailedState = "In Progress",
        HomeTeam = "Home",
        AwayTeam = "Away",
        HomeTeamId = 222,
        AwayTeamId = 111,
        HomeBattingOrder = [1, 2, 3],
        AwayPitchers = [9],
        PlayerStats = { [1] = new PlayerGameStats { Batting = new PlayerBattingStats { AtBats = 2 } } },
    };

    [Fact]
    public void Clone_CopiesListsSoMutationDoesNotLeak()
    {
        var original = Sample();
        var clone = original.Clone();

        clone.HomeBattingOrder.Add(4);
        clone.AwayPitchers[0] = 10;

        Assert.Equal([1, 2, 3], original.HomeBattingOrder);
        Assert.Equal([9], original.AwayPitchers);
    }

    [Fact]
    public void Clone_CopiesPlayerStatsDeeply()
    {
        var original = Sample();
        var clone = original.Clone();

        clone.PlayerStats[1].Batting!.AtBats = 99;
        clone.PlayerStats[2] = new PlayerGameStats();

        Assert.Equal(2, original.PlayerStats[1].Batting!.AtBats);
        Assert.False(original.PlayerStats.ContainsKey(2));
    }

    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(Sample(), Sample());
        Assert.Equal(Sample().GetHashCode(), Sample().GetHashCode());
        Assert.Equal(Sample(), Sample().Clone());
    }

    [Fact]
    public void Equality_DistinguishesListOrder()
    {
        var a = Sample();
        var b = Sample();
        b.HomeBattingOrder = [3, 2, 1];
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_DistinguishesNestedStats()
    {
        var a = Sample();
        var b = Sample();
        b.PlayerStats[1].Batting!.AtBats = 3;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BattingFormatted_NullWithoutAtBats()
    {
        Assert.Null(new PlayerBattingStats().Formatted);
    }

    [Fact]
    public void BattingFormatted_NullWhenNoActivity()
    {
        Assert.Null(new PlayerBattingStats { AtBats = 0 }.Formatted);
    }

    [Theory]
    [InlineData(0, 0, 1, 0, "0-0 · BB")]           // walk with no at-bat still counts as activity
    [InlineData(0, 0, 0, 1, "0-0 · SB")]
    public void BattingFormatted_ActivityWithoutAtBats(
        int atBats, int hits, int walks, int steals, string expected)
    {
        var stats = new PlayerBattingStats
        {
            AtBats = atBats, Hits = hits, BaseOnBalls = walks, StolenBases = steals,
        };
        Assert.Equal(expected, stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_MatchesLegacyOutput()
    {
        // LiveFeedPatcherTests.swift:171-180
        var stats = new PlayerBattingStats
        {
            AtBats = 4, Hits = 2, HomeRuns = 1, Rbi = 2, Runs = 1,
        };
        Assert.Equal("2-4 · HR, 2 RBI, 1 R", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_PluralizesCountsAboveOne()
    {
        var stats = new PlayerBattingStats
        {
            AtBats = 5, Hits = 4, Doubles = 2, Triples = 2, HomeRuns = 2,
            Rbi = 1, Runs = 1, BaseOnBalls = 2, StolenBases = 2,
        };
        Assert.Equal("4-5 · 2 2B, 2 3B, 2 HR, 1 RBI, 1 R, 2 BB, 2 SB", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_SingularExtrasOmitTheCount()
    {
        var stats = new PlayerBattingStats
        {
            AtBats = 3, Hits = 1, Doubles = 1, BaseOnBalls = 1, StolenBases = 1,
        };
        Assert.Equal("1-3 · 2B, BB, SB", stats.Formatted);
    }

    [Fact]
    public void BattingFormatted_NoExtrasIsBareLine()
    {
        Assert.Equal("0-4", new PlayerBattingStats { AtBats = 4, Hits = 0 }.Formatted);
    }

    [Fact]
    public void PitchingFormatted_NullWhenNotYetPitched()
    {
        Assert.Null(new PlayerPitchingStats().Formatted);
        Assert.Null(new PlayerPitchingStats { InningsPitched = "0.0" }.Formatted);
    }

    [Fact]
    public void PitchingFormatted_MatchesLegacyOutput()
    {
        // LiveFeedPatcherTests.swift:182-190
        var stats = new PlayerPitchingStats
        {
            InningsPitched = "6.1", StrikeOuts = 7, EarnedRuns = 2, NumberOfPitches = 98,
        };
        Assert.Equal("6.1 IP, 7K, 2ER, 98P", stats.Formatted);
    }

    [Fact]
    public void PitchingFormatted_IncludesZeroEarnedRunsButDropsZeroKAndPitches()
    {
        var stats = new PlayerPitchingStats
        {
            InningsPitched = "2.0", StrikeOuts = 0, EarnedRuns = 0, NumberOfPitches = 0,
        };
        Assert.Equal("2.0 IP, 0ER", stats.Formatted);
    }

    [Fact]
    public void PitchingFormatted_OmitsEarnedRunsWhenNull()
    {
        Assert.Equal("1.0 IP", new PlayerPitchingStats { InningsPitched = "1.0" }.Formatted);
    }
}
