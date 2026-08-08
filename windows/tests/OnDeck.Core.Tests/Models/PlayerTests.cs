using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class PlayerTests
{
    private static Player Make(
        PlayerPosition[] positions,
        string[] fantraxPositions,
        RosterStatus status = RosterStatus.Active) =>
        new(660271, "Shohei Ohtani", "LAD",
            new HashSet<PlayerPosition>(positions),
            new HashSet<string>(fantraxPositions),
            status);

    [Fact]
    public void IsPitcher_TrueWhenPositionsContainPitcher()
    {
        Assert.True(Make([PlayerPosition.Pitcher], ["SP"]).IsPitcher);
        Assert.False(Make([PlayerPosition.Hitter], ["DH"]).IsPitcher);
    }

    [Fact]
    public void IsHitter_TrueWhenPositionsContainHitter()
    {
        Assert.True(Make([PlayerPosition.Hitter], ["DH"]).IsHitter);
        Assert.False(Make([PlayerPosition.Pitcher], ["SP"]).IsHitter);
    }

    [Fact]
    public void TwoWayPlayer_IsBothPitcherAndHitter()
    {
        var ohtani = Make([PlayerPosition.Hitter, PlayerPosition.Pitcher], ["SP", "DH"]);
        Assert.True(ohtani.IsPitcher);
        Assert.True(ohtani.IsHitter);
    }

    [Theory]
    [InlineData(RosterStatus.Active, false, false)]
    [InlineData(RosterStatus.Reserve, true, false)]
    [InlineData(RosterStatus.InjuredReserve, false, true)]
    [InlineData(RosterStatus.Minors, false, true)]
    public void RosterStatus_DrivesBenchAndUnavailable(
        RosterStatus status, bool expectedBench, bool expectedUnavailable)
    {
        var player = Make([PlayerPosition.Hitter], ["OF"], status);
        Assert.Equal(expectedBench, player.IsOnBench);
        Assert.Equal(expectedUnavailable, player.IsUnavailable);
    }

    [Fact]
    public void RosterStatus_WireValuesMatchFantraxStatusIds()
    {
        Assert.Equal(1, (int)RosterStatus.Active);
        Assert.Equal(2, (int)RosterStatus.Reserve);
        Assert.Equal(3, (int)RosterStatus.InjuredReserve);
        Assert.Equal(9, (int)RosterStatus.Minors);
    }

    [Fact]
    public void IsStartingPitcherOnly_TrueForSpWithoutRpAndNotHitter()
    {
        Assert.True(Make([PlayerPosition.Pitcher], ["SP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseWhenAlsoReliever()
    {
        Assert.False(Make([PlayerPosition.Pitcher], ["SP", "RP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseForTwoWayPlayer()
    {
        Assert.False(Make([PlayerPosition.Hitter, PlayerPosition.Pitcher], ["SP", "DH"])
            .IsStartingPitcherOnly);
    }

    [Fact]
    public void IsStartingPitcherOnly_FalseForRelieverOnly()
    {
        Assert.False(Make([PlayerPosition.Pitcher], ["RP"]).IsStartingPitcherOnly);
    }

    [Fact]
    public void Equality_IsStructuralAcrossDistinctSetInstances()
    {
        var a = Make([PlayerPosition.Hitter], ["OF", "DH"]);
        var b = Make([PlayerPosition.Hitter], ["DH", "OF"]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_DistinguishesDifferentFantraxPositions()
    {
        var a = Make([PlayerPosition.Pitcher], ["SP"]);
        var b = Make([PlayerPosition.Pitcher], ["RP"]);
        Assert.NotEqual(a, b);
    }
}
