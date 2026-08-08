using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.Models;

public class PlayerStateTests
{
    private static PlayerState.GameContext Context(PlayerState.ActiveRole role) =>
        new(GamePk: 776543, Role: role, Inning: "Top 3rd",
            HomeTeam: "Los Angeles Dodgers", AwayTeam: "San Francisco Giants",
            HomeTeamId: 119, AwayTeamId: 137,
            HomeScore: 2, AwayScore: 1,
            Balls: 1, Strikes: 2, Outs: 1,
            RunnerOnFirst: true, RunnerOnSecond: false, RunnerOnThird: false);

    [Fact]
    public void Active_CarriesGameContext()
    {
        PlayerState state = new PlayerState.Active(Context(PlayerState.ActiveRole.Batting));

        var active = Assert.IsType<PlayerState.Active>(state);
        Assert.Equal(776543, active.Context.GamePk);
        Assert.Equal(PlayerState.ActiveRole.Batting, active.Context.Role);
        Assert.Equal("Top 3rd", active.Context.Inning);
        Assert.True(active.Context.RunnerOnFirst);
    }

    [Fact]
    public void Upcoming_CarriesStartTime()
    {
        var start = new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero);
        PlayerState state = new PlayerState.Upcoming(start);

        Assert.Equal(start, Assert.IsType<PlayerState.Upcoming>(state).StartTime);
    }

    [Fact]
    public void Inactive_GameOver_CarriesGamePk()
    {
        PlayerState state = new PlayerState.Inactive(new PlayerState.InactiveReason.GameOver(776543));

        var reason = Assert.IsType<PlayerState.Inactive>(state).Reason;
        Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.GameOver>(reason).GamePk);
    }

    [Fact]
    public void Inactive_Substituted_CarriesGamePk()
    {
        PlayerState state = new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(776543));

        var reason = Assert.IsType<PlayerState.Inactive>(state).Reason;
        Assert.Equal(776543, Assert.IsType<PlayerState.InactiveReason.Substituted>(reason).GamePk);
    }

    [Fact]
    public void Inactive_DayOff_HasNoPayloadAndComparesEqual()
    {
        PlayerState.InactiveReason a = new PlayerState.InactiveReason.DayOff();
        PlayerState.InactiveReason b = new PlayerState.InactiveReason.DayOff();

        Assert.Equal(a, b);
    }

    [Fact]
    public void Cases_AreDistinguishableByPatternMatch()
    {
        PlayerState[] states =
        [
            new PlayerState.Active(Context(PlayerState.ActiveRole.Pitching)),
            new PlayerState.Upcoming(DateTimeOffset.UnixEpoch),
            new PlayerState.Inactive(new PlayerState.InactiveReason.DayOff()),
        ];

        var labels = states.Select(s => s switch
        {
            PlayerState.Active => "active",
            PlayerState.Upcoming => "upcoming",
            PlayerState.Inactive => "inactive",
            _ => "unknown",
        });

        Assert.Equal(["active", "upcoming", "inactive"], labels);
    }

    [Fact]
    public void GameContext_HasValueEquality()
    {
        Assert.Equal(Context(PlayerState.ActiveRole.Batting), Context(PlayerState.ActiveRole.Batting));
        Assert.NotEqual(Context(PlayerState.ActiveRole.Batting), Context(PlayerState.ActiveRole.Pitching));
    }
}
