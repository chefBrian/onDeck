using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class DisplayRulesProximityTests
{
    private static Player Hitter(int id) =>
        new(id, $"Player {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player PitcherOnly(int id) =>
        new(id, $"Pitcher {id}", "Los Angeles Dodgers",
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    /// <summary>Home team batting in the bottom half, current batter is 101.</summary>
    private static LiveFeedData HomeBatting(int currentBatterId = 101) => new()
    {
        GameState = "Live",
        DetailedState = "In Progress",
        InningHalf = "Bottom",
        InningState = "Bottom",
        CurrentBatterId = currentBatterId,
        HomeBattingOrder = [101, 102, 103, 104, 105, 106, 107, 108, 109],
        AwayBattingOrder = [201, 202, 203],
    };

    [Fact]
    public void ProximityFor_IsNullWithoutAFeed()
    {
        Assert.Null(DisplayRules.ProximityFor(Hitter(101), null));
    }

    [Fact]
    public void ProximityFor_IsNullForPitcherOnlyPlayers()
    {
        var feed = HomeBatting();
        feed.HomeBattingOrder = [901, 102, 103];

        Assert.Null(DisplayRules.ProximityFor(PitcherOnly(901), feed));
    }

    [Fact]
    public void ProximityFor_IsNullWhenThePlayerIsInNeitherBattingOrder()
    {
        Assert.Null(DisplayRules.ProximityFor(Hitter(999), HomeBatting()));
    }

    [Fact]
    public void ProximityFor_MapsDistanceZeroToAtBat()
    {
        Assert.Equal(BattingProximityKind.AtBat, DisplayRules.ProximityFor(Hitter(101), HomeBatting())!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_MapsDistanceOneToOnDeckAndTwoToDueUp()
    {
        Assert.Equal(BattingProximityKind.OnDeck, DisplayRules.ProximityFor(Hitter(102), HomeBatting())!.Value.Kind);
        Assert.Equal(BattingProximityKind.DueUp, DisplayRules.ProximityFor(Hitter(103), HomeBatting())!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_MapsFurtherDistancesToOrder()
    {
        var proximity = DisplayRules.ProximityFor(Hitter(105), HomeBatting())!.Value;

        Assert.Equal(BattingProximityKind.Order, proximity.Kind);
        Assert.Equal(4, proximity.Value);
    }

    [Fact]
    public void ProximityFor_WrapsSoTheJustBattedHitterSinks()
    {
        // 109 batted immediately before 101 - distance 8, the bottom of the live band.
        var proximity = DisplayRules.ProximityFor(Hitter(109), HomeBatting())!.Value;

        Assert.Equal(BattingProximityKind.Order, proximity.Kind);
        Assert.Equal(8, proximity.Value);
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingBetweenHalfInnings()
    {
        // MLB keeps currentBatter/inningHalf as a stale holdover during the break, so the
        // third-out hitter would still look at bat.
        var feed = HomeBatting();
        feed.InningState = "Middle";

        var proximity = DisplayRules.ProximityFor(Hitter(101), feed)!.Value;

        Assert.Equal(BattingProximityKind.NotBatting, proximity.Kind);
        Assert.Equal(0, proximity.Value);       // lineup spot index
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingWhenTheOtherTeamIsUp()
    {
        var feed = HomeBatting();
        feed.InningHalf = "Top";
        feed.InningState = "Top";

        var proximity = DisplayRules.ProximityFor(Hitter(103), feed)!.Value;

        Assert.Equal(BattingProximityKind.NotBatting, proximity.Kind);
        Assert.Equal(2, proximity.Value);
    }

    [Fact]
    public void ProximityFor_FallsToNotBattingWhenTheCurrentBatterIsUnknown()
    {
        var feed = HomeBatting();
        feed.CurrentBatterId = null;

        Assert.Equal(BattingProximityKind.NotBatting, DisplayRules.ProximityFor(Hitter(103), feed)!.Value.Kind);
    }

    [Fact]
    public void ProximityFor_ReadsTheAwayOrderForAwayHitters()
    {
        var feed = HomeBatting();
        feed.InningHalf = "Top";
        feed.InningState = "Top";
        feed.CurrentBatterId = 201;

        Assert.Equal(BattingProximityKind.OnDeck, DisplayRules.ProximityFor(Hitter(202), feed)!.Value.Kind);
    }

    [Theory]
    [InlineData(BattingProximityKind.AtBat, 0, 0)]
    [InlineData(BattingProximityKind.OnDeck, 0, 1)]
    [InlineData(BattingProximityKind.DueUp, 0, 2)]
    [InlineData(BattingProximityKind.Order, 5, 5)]
    [InlineData(BattingProximityKind.NotBatting, 3, 53)]
    public void SortKey_PutsNotBattingInItsOwnBandAboveTheLiveOnes(
        BattingProximityKind kind, int value, int expected)
    {
        var proximity = kind switch
        {
            BattingProximityKind.AtBat => BattingProximity.AtBat,
            BattingProximityKind.OnDeck => BattingProximity.OnDeck,
            BattingProximityKind.DueUp => BattingProximity.DueUp,
            BattingProximityKind.Order => BattingProximity.Order(value),
            _ => BattingProximity.NotBatting(value),
        };

        Assert.Equal(expected, proximity.SortKey);
    }
}
