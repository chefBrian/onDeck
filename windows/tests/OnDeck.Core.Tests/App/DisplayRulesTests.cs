using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class DisplayRulesTests
{
    private static Player Hitter(int id = 101, string team = "Los Angeles Dodgers") =>
        new(id, $"Player {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player PitcherOnly(int id = 901, string team = "Los Angeles Dodgers") =>
        new(id, $"Pitcher {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    private static Game DodgersHome(
        IReadOnlyList<int>? homeLineup = null, IReadOnlyList<int>? awayLineup = null) =>
        new(1, "Los Angeles Dodgers", "San Francisco Giants", 119, 137,
            new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero),
            null, null, [], homeLineup ?? [], awayLineup ?? []);

    private static LiveFeedData Feed(string detailedState = "In Progress") => new()
    {
        GameState = "Live",
        DetailedState = detailedState,
        InningHalf = "Bottom",
        InningState = "Bottom",
        CurrentBatterId = 101,
        HomeBattingOrder = [101, 102, 103],
    };

    // MARK: - InGameSortKey

    [Fact]
    public void InGameSortKey_UsesProximityWhenThePlayerHasOne()
    {
        var key = DisplayRules.InGameSortKey(
            Hitter(102), DodgersHome(), Feed(), null, BattingProximity.OnDeck);

        Assert.Equal(1, key);
    }

    [Fact]
    public void InGameSortKey_PutsThePitcherOnTheMoundInTheLiveBand()
    {
        var feed = Feed();
        feed.CurrentPitcherId = 901;

        Assert.Equal(0, DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), feed, null, null));
    }

    [Fact]
    public void InGameSortKey_PutsOtherPitchersAboveNotBattingHitters()
    {
        Assert.Equal(70, DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), Feed(), null, null));
    }

    [Fact]
    public void InGameSortKey_AddsOneHundredForAMidGameDelay()
    {
        Assert.Equal(
            101,
            DisplayRules.InGameSortKey(
                Hitter(102), DodgersHome(), Feed("Delayed: Rain"), null, BattingProximity.OnDeck));
    }

    [Fact]
    public void InGameSortKey_AddsOneHundredForASuspension()
    {
        Assert.Equal(
            170,
            DisplayRules.InGameSortKey(PitcherOnly(), DodgersHome(), Feed("Suspended: Rain"), null, null));
    }

    [Fact]
    public void InGameSortKey_AddsTwoHundredWhenTheFiledCardExcludesThePlayer()
    {
        var lineup = new GameLineup { Home = [102, 103] };

        Assert.Equal(
            250,
            DisplayRules.InGameSortKey(
                Hitter(199), DodgersHome(), Feed(), lineup, BattingProximity.NotBatting(0)));
    }

    [Fact]
    public void InGameSortKey_ExclusionOutranksDelay()
    {
        var lineup = new GameLineup { Home = [102, 103] };

        Assert.Equal(
            270,
            DisplayRules.InGameSortKey(Hitter(199), DodgersHome(), Feed("Delayed: Rain"), lineup, null));
    }

    [Fact]
    public void InGameSortKey_FallsBackWhenThePlayerHasNoGame()
    {
        Assert.Equal(70, DisplayRules.InGameSortKey(Hitter(), null, null, null, null));
        Assert.Equal(2, DisplayRules.InGameSortKey(Hitter(), null, null, null, BattingProximity.DueUp));
    }

    // MARK: - IsInLineup

    [Fact]
    public void IsInLineup_AssumesInUntilThatSidesCardIsFiled()
    {
        Assert.True(DisplayRules.IsInLineup(Hitter(199), DodgersHome(), null));
        Assert.True(DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup()));
    }

    [Fact]
    public void IsInLineup_IsFalseWhenTheFiledCardOmitsTheHitter()
    {
        Assert.False(
            DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup { Home = [101, 102] }));
    }

    [Fact]
    public void IsInLineup_IgnoresTheOpponentsCard()
    {
        Assert.True(
            DisplayRules.IsInLineup(Hitter(199), DodgersHome(), new GameLineup { Away = [201, 202] }));
    }

    // MARK: - Stat lines

    [Fact]
    public void RawStatLine_UsesPitchingStatsForPitcherOnlyPlayers()
    {
        var feed = Feed();
        feed.PlayerStats[901] = new PlayerGameStats
        {
            Pitching = new PlayerPitchingStats { InningsPitched = "5.0", StrikeOuts = 7, EarnedRuns = 1 },
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("5.0 IP, 7K, 1ER", DisplayRules.RawStatLine(PitcherOnly(), feed));
    }

    [Fact]
    public void RawStatLine_UsesBattingStatsForEveryoneElse()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 3, Hits = 2, HomeRuns = 1, Rbi = 2 },
        };

        Assert.Equal("2-3 · HR, 2 RBI", DisplayRules.RawStatLine(Hitter(), feed));
    }

    [Fact]
    public void RawStatLine_IsNullWithoutStats()
    {
        Assert.Null(DisplayRules.RawStatLine(Hitter(), Feed()));
        Assert.Null(DisplayRules.RawStatLine(Hitter(), null));
    }

    [Fact]
    public void LiveStatLine_ReportsNotInLineupAboveEverythingElse()
    {
        Assert.Equal("Not in Lineup", DisplayRules.LiveStatLine(Hitter(), Feed(), false, BattingProximity.AtBat));
    }

    [Fact]
    public void LiveStatLine_PrefixesOnDeckAndInHole()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("On Deck · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.OnDeck));
        Assert.Equal("In Hole · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.DueUp));
    }

    [Fact]
    public void LiveStatLine_HasNoPrefixAtBatOrDeeperInTheOrder()
    {
        var feed = Feed();
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.AtBat));
        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.Order(5)));
        Assert.Equal("1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, null));
    }

    [Fact]
    public void LiveStatLine_IsJustThePrefixWithoutAStatLine()
    {
        Assert.Equal("On Deck", DisplayRules.LiveStatLine(Hitter(), Feed(), true, BattingProximity.OnDeck));
    }

    [Fact]
    public void LiveStatLine_LeadsWithTheDelayLabel()
    {
        var feed = Feed("Delayed: Rain");
        feed.PlayerStats[101] = new PlayerGameStats
        {
            Batting = new PlayerBattingStats { AtBats = 2, Hits = 1 },
        };

        Assert.Equal("Rain Delay · 1-2", DisplayRules.LiveStatLine(Hitter(), feed, true, BattingProximity.OnDeck));
    }

    [Fact]
    public void LiveStatLine_IsTheDelayLabelAloneWithoutStats()
    {
        Assert.Equal("Rain Delay", DisplayRules.LiveStatLine(Hitter(), Feed("Delayed: Rain"), true, null));
    }

    [Fact]
    public void LiveStatLine_IsNullWithoutAFeed()
    {
        Assert.Null(DisplayRules.LiveStatLine(Hitter(), null, true, null));
    }

    [Theory]
    [InlineData("Delayed: Rain", "Rain Delay")]
    [InlineData("Suspended: Rain", "Suspended: Rain")]
    [InlineData("Delayed", "Delayed")]
    [InlineData("Suspended", "Suspended")]
    [InlineData("In Progress", null)]
    [InlineData(null, null)]
    public void DelayLabel_MatchesTheSwiftForms(string? detailedState, string? expected)
    {
        Assert.Equal(expected, DisplayRules.DelayLabel(detailedState));
    }

    [Theory]
    [InlineData("Delayed: Rain", DelayIndicator.Rain)]
    [InlineData("Delayed Start: Rain", DelayIndicator.Rain)]
    [InlineData("Suspended: Darkness", DelayIndicator.Delayed)]
    [InlineData("Delayed", DelayIndicator.Delayed)]
    [InlineData("Postponed", DelayIndicator.Postponed)]
    [InlineData("In Progress", DelayIndicator.None)]
    [InlineData(null, DelayIndicator.None)]
    public void DelayFor_ClassifiesTheDetailedState(string? detailedState, DelayIndicator expected)
    {
        Assert.Equal(expected, DisplayRules.DelayFor(detailedState));
    }

    // MARK: - LineupInfoFor

    [Fact]
    public void LineupInfoFor_IsUnknownBeforeThatSidesCardIsFiled()
    {
        Assert.Equal(
            LineupInfoKind.Unknown,
            DisplayRules.LineupInfoFor(Hitter(199), DodgersHome(), new GameLineup(), null).Kind);
    }

    [Fact]
    public void LineupInfoFor_IsNotInLineupWhenTheFiledCardOmitsTheHitter()
    {
        Assert.Equal(
            LineupInfoKind.NotInLineup,
            DisplayRules.LineupInfoFor(
                Hitter(199), DodgersHome(), new GameLineup { Home = [101, 102] }, null).Kind);
    }

    [Fact]
    public void LineupInfoFor_ReadsTheBattingOrderSpotFromTheFeedFirst()
    {
        var feed = Feed();
        feed.HomeBattingOrder = [105, 101, 103];
        var info = DisplayRules.LineupInfoFor(
            Hitter(), DodgersHome(homeLineup: [101, 105, 103]), new GameLineup { Home = [101, 105, 103] }, feed);

        Assert.Equal(LineupInfoKind.BattingOrder, info.Kind);
        Assert.Equal(2, info.Spot);
    }

    [Fact]
    public void LineupInfoFor_FallsBackToTheScheduleLineup()
    {
        var info = DisplayRules.LineupInfoFor(
            Hitter(), DodgersHome(homeLineup: [105, 103, 101]), new GameLineup { Home = [105, 103, 101] }, null);

        Assert.Equal(LineupInfoKind.BattingOrder, info.Kind);
        Assert.Equal(3, info.Spot);
    }

    [Fact]
    public void LineupInfoFor_IsInLineupWhenListedWithoutAKnownSpot()
    {
        // Probable starter: on the pitchers set, so not excluded, but on no batting order.
        var lineup = new GameLineup { Home = [101, 102], HomePitchers = [901] };

        Assert.Equal(
            LineupInfoKind.InLineup,
            DisplayRules.LineupInfoFor(PitcherOnly(), DodgersHome(), lineup, null).Kind);
    }

    [Fact]
    public void LineupInfoFor_IsUnknownWhenThePlayerHasNoGame()
    {
        Assert.Equal(LineupInfoKind.Unknown, DisplayRules.LineupInfoFor(Hitter(), null, null, null).Kind);
    }
}
