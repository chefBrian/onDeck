using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorFeedTests
{
    private static Player Pitcher(int id, string team = "Los Angeles Dodgers") =>
        new(id, $"Pitcher {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Pitcher },
            new HashSet<string> { "SP" },
            RosterStatus.Active);

    private static LiveFeedData LiveFeed(
        string detailedState = "In Progress",
        int? batter = null,
        int? pitcher = null,
        string? inningState = null,
        int? inning = 3,
        string half = "Top") => new()
    {
        GameState = "Live",
        DetailedState = detailedState,
        CurrentBatterId = batter,
        CurrentPitcherId = pitcher,
        Inning = inning,
        InningHalf = half,
        InningState = inningState,
        HomeTeam = "Los Angeles Dodgers",
        AwayTeam = "San Francisco Giants",
        HomeTeamId = 119,
        AwayTeamId = 137,
    };

    private static (GameMonitor Monitor, StateManager States) Started(params Player[] players)
    {
        var (monitor, _, _) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.TrackGames([GameAt(1, Now)], players);
        return (monitor, states);
    }

    [Fact]
    public void ProcessFeed_TracksLineupsPerSideAndFiresTheCallback()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = new List<int>();
        monitor.OnLineupUpdate = fired.Add;

        var feed = LiveFeed();
        feed.HomeBattingOrder = [10, 11];
        feed.AwayBattingOrder = [20];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now, homePitcher: 30));

        var lineup = monitor.LineupPlayerIds[1];
        Assert.Equal(new HashSet<int> { 10, 11 }, lineup.Home);
        Assert.Equal(new HashSet<int> { 20 }, lineup.Away);
        Assert.Contains(30, lineup.HomePitchers);       // probable pitcher folded in
        Assert.Equal([1], fired);
    }

    [Fact]
    public void ProcessFeed_EmptyLineupSideDoesNotOverwriteWhatWeHad()
    {
        var (monitor, _) = Started(Hitter(10));
        var first = LiveFeed();
        first.HomeBattingOrder = [10, 11];
        monitor.ProcessFeed(first, 1, GameAt(1, Now));

        var second = LiveFeed();
        second.HomeBattingOrder = [];
        monitor.ProcessFeed(second, 1, GameAt(1, Now));

        Assert.Equal(new HashSet<int> { 10, 11 }, monitor.LineupPlayerIds[1].Home);
    }

    [Fact]
    public void ProcessFeed_DoesNotFireLineupUpdateWhenNothingChanged()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = 0;
        monitor.OnLineupUpdate = _ => fired++;

        var feed = LiveFeed();
        feed.HomeBattingOrder = [10];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Equal(1, fired);
    }

    [Theory]
    [InlineData("In Progress", true)]
    [InlineData("Delayed: Rain", true)]
    [InlineData("Suspended: Rain", true)]
    [InlineData("Manager challenge", true)]
    [InlineData("Warmup", false)]
    [InlineData("Game Over", false)]
    [InlineData("Pre-Game", false)]
    public void ProcessFeed_OnlyPlayableStatesCountAsLive(string detailedState, bool expected)
    {
        var (monitor, _) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(detailedState, batter: 10), 1, GameAt(1, Now));

        Assert.Equal(expected, monitor.IsLive(1));
    }

    [Fact]
    public void ProcessFeed_FiresGameStartOnceOnly()
    {
        var (monitor, _) = Started(Hitter(10));
        var fired = new List<int>();
        monitor.OnGameStart = fired.Add;

        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        Assert.Equal([1], fired);
    }

    [Fact]
    public void ProcessFeed_MarksRosteredBatterActive()
    {
        var (monitor, states) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        var active = Assert.IsType<PlayerState.Active>(states.PlayerStates[10]);
        Assert.Equal(PlayerState.ActiveRole.Batting, active.Context.Role);
        Assert.Equal("Top 3", active.Context.Inning);
        Assert.Equal("Dodgers", active.Context.HomeTeam);
        Assert.Equal("Giants", active.Context.AwayTeam);
    }

    [Fact]
    public void ProcessFeed_MarksRosteredPitcherActive()
    {
        var (monitor, states) = Started(Pitcher(30));

        monitor.ProcessFeed(LiveFeed(pitcher: 30), 1, GameAt(1, Now));

        var active = Assert.IsType<PlayerState.Active>(states.PlayerStates[30]);
        Assert.Equal(PlayerState.ActiveRole.Pitching, active.Context.Role);
    }

    [Fact]
    public void ProcessFeed_IgnoresPitcherOnlyPlayersAppearingAsBatter()
    {
        var (monitor, states) = Started(Pitcher(30));

        monitor.ProcessFeed(LiveFeed(batter: 30), 1, GameAt(1, Now));

        Assert.False(states.PlayerStates.ContainsKey(30));
    }

    [Fact]
    public void ProcessFeed_FlipsActivePlayersToUpcomingBetweenHalfInnings()
    {
        var (monitor, states) = Started(Hitter(10));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed(batter: 10, inningState: "Middle"), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
    }

    [Fact]
    public void ProcessFeed_FlipsActivePlayersToUpcomingDuringADelay()
    {
        var (monitor, states) = Started(Hitter(10));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed("Delayed: Rain", batter: 10), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
    }

    [Fact]
    public void ProcessFeed_RevertsThePreviousBatterWhenTheAtBatEnds()
    {
        var (monitor, states) = Started(Hitter(10), Hitter(11));
        monitor.ProcessFeed(LiveFeed(batter: 10), 1, GameAt(1, Now));

        monitor.ProcessFeed(LiveFeed(batter: 11), 1, GameAt(1, Now));

        Assert.IsType<PlayerState.Upcoming>(states.PlayerStates[10]);
        Assert.IsType<PlayerState.Active>(states.PlayerStates[11]);
    }

    [Fact]
    public void ProcessFeed_SubstitutesTheOutgoingPitcherOnASide()
    {
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var first = LiveFeed(pitcher: 30);
        first.HomePitchers = [30];
        monitor.ProcessFeed(first, 1, GameAt(1, Now));

        var second = LiveFeed(pitcher: 31);
        second.HomePitchers = [30, 31];
        monitor.ProcessFeed(second, 1, GameAt(1, Now));

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[30]);
        Assert.Equal(1, Assert.IsType<PlayerState.InactiveReason.Substituted>(inactive.Reason).GamePk);
    }

    [Fact]
    public void ProcessFeed_CatchAllSubstitutesEarlierPitchersWithAStatLine()
    {
        // Handles app restarts and missed transitions: pitchers are ordered by appearance,
        // so anyone before the last entry who actually pitched has been substituted.
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var feed = LiveFeed(pitcher: 31);
        feed.HomePitchers = [30, 31];
        feed.PlayerStats[30] = new PlayerGameStats
        {
            Pitching = new PlayerPitchingStats { InningsPitched = "5.0" },
        };
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[30]);
        Assert.IsType<PlayerState.InactiveReason.Substituted>(inactive.Reason);
    }

    [Fact]
    public void ProcessFeed_CatchAllSkipsPitchersWithoutAStatLine()
    {
        var (monitor, states) = Started(Pitcher(30), Pitcher(31));

        var feed = LiveFeed(pitcher: 31);
        feed.HomePitchers = [30, 31];
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.False(states.PlayerStates.ContainsKey(30));
    }

    [Fact]
    public void ProcessFeed_StoresCompletedPlayDescriptionsForRosterPlayers()
    {
        var (monitor, _) = Started(Hitter(10), Pitcher(30));

        var feed = LiveFeed(batter: 10, pitcher: 30);
        feed.IsPlayComplete = true;
        feed.LastPlayDescription = "Player 10 singles on a line drive.";
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Equal("Player 10 singles on a line drive.", monitor.LastPlayDescriptions[10]);
        Assert.Equal("Player 10 singles on a line drive.", monitor.LastPlayDescriptions[30]);
    }

    [Fact]
    public void ProcessFeed_IgnoresIncompletePlays()
    {
        var (monitor, _) = Started(Hitter(10));

        var feed = LiveFeed(batter: 10);
        feed.IsPlayComplete = false;
        feed.LastPlayDescription = "in progress";
        monitor.ProcessFeed(feed, 1, GameAt(1, Now));

        Assert.Empty(monitor.LastPlayDescriptions);
    }

    [Fact]
    public void ProcessFeed_FormatsInningAsTopOrBot()
    {
        var (monitor, states) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(batter: 10, inning: 7, half: "Bottom"), 1, GameAt(1, Now));

        Assert.Equal("Bot 7", Assert.IsType<PlayerState.Active>(states.PlayerStates[10]).Context.Inning);
    }

    [Fact]
    public void ProcessFeed_LeavesInningBlankWhenTheFeedHasNoInning()
    {
        var (monitor, states) = Started(Hitter(10));

        monitor.ProcessFeed(LiveFeed(batter: 10, inning: null), 1, GameAt(1, Now));

        Assert.Equal("", Assert.IsType<PlayerState.Active>(states.PlayerStates[10]).Context.Inning);
    }
}
