using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class GameLineupTests
{
    private const int OhtaniId = 660271;
    private const int BettsId = 605141;
    private const int SnellId = 605483;

    private static Player Hitter(int id) =>
        new(id, "Hitter", "LAD",
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    private static Player Reliever(int id) =>
        new(id, "Reliever", "LAD",
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "RP" },
            RosterStatus.Active);

    [Fact]
    public void IsSubmitted_FalseWhenBattingCardEmpty()
    {
        var lineup = new GameLineup { HomePitchers = [SnellId] };
        Assert.False(lineup.IsSubmitted(Game.Side.Home));
    }

    [Fact]
    public void IsSubmitted_TrueOnlyForTheSideThatFiled()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.True(lineup.IsSubmitted(Game.Side.Home));
        Assert.False(lineup.IsSubmitted(Game.Side.Away));
    }

    [Fact]
    public void Ids_ReturnsUnionOfBattersAndPitchers()
    {
        var lineup = new GameLineup { Home = [OhtaniId], HomePitchers = [SnellId] };

        var ids = lineup.Ids(Game.Side.Home);
        Assert.Equal(2, ids.Count);
        Assert.Contains(OhtaniId, ids);
        Assert.Contains(SnellId, ids);
        Assert.Empty(lineup.Ids(Game.Side.Away));
    }

    [Fact]
    public void Excludes_TrueForHitterMissingFromFiledCard()
    {
        var lineup = new GameLineup { Home = [BettsId] };
        Assert.True(lineup.Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseForHitterPresentOnCard()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.False(lineup.Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseBeforeCardIsFiled()
    {
        Assert.False(new GameLineup().Excludes(Hitter(OhtaniId), Game.Side.Home));
    }

    [Fact]
    public void Excludes_FalseForPitchers_RelieversAreNeverOnTheCard()
    {
        var lineup = new GameLineup { Home = [OhtaniId, BettsId] };
        Assert.False(lineup.Excludes(Reliever(SnellId), Game.Side.Home));
    }

    [Fact]
    public void Equality_ComparesSetContents()
    {
        var a = new GameLineup { Home = [OhtaniId, BettsId], AwayPitchers = [SnellId] };
        var b = new GameLineup { Home = [BettsId, OhtaniId], AwayPitchers = [SnellId] };
        var c = new GameLineup { Home = [OhtaniId], AwayPitchers = [SnellId] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }
}
