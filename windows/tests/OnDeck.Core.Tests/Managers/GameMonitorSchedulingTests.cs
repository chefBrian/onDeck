using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorSchedulingTests
{
    [Fact]
    public void NextEventDelay_IsZeroWhenAGameIsAlreadyInTheActiveWindow()
    {
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddMinutes(10))], [Hitter(10)]);

        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_CountsDownToTheNextMilestone()
    {
        // Start in 3h: the 2h milestone fires in 1h.
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddHours(3))], [Hitter(10)]);

        Assert.Equal(TimeSpan.FromHours(1), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_IsZeroWhenAMilestoneIsAlreadyDue()
    {
        // Start in 90 min: the 2h milestone has passed and is uncompleted.
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddMinutes(90))], [Hitter(10)]);

        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_FallsToTheNextMilestoneOnceOneIsConsumed()
    {
        // Start in 90 min: consume the 2h milestone, then the 1h milestone is 30 min out.
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddMinutes(90))], [Hitter(10)]);
        monitor.SelectGamesToPoll();

        Assert.Equal(TimeSpan.FromMinutes(30), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_TakesTheEarliestAcrossGames()
    {
        var (monitor, _, _) = Create();
        monitor.TrackGames(
            [GameAt(1, Now.AddHours(6)), GameAt(2, Now.AddHours(3))], [Hitter(10)]);

        // Game 2's 2h milestone is 1h out; game 1's earliest is 4h out.
        Assert.Equal(TimeSpan.FromHours(1), monitor.NextEventDelay());
        monitor.StopMonitoring();
    }

    [Fact]
    public void NextEventDelay_IsZeroWithNoGames()
    {
        var (monitor, _, _) = Create();
        Assert.Equal(TimeSpan.Zero, monitor.NextEventDelay());
    }

    [Fact]
    public void SelectGamesToPoll_IncludesGamesInsideTheActiveWindow()
    {
        var (monitor, _, _) = Create();
        monitor.TrackGames(
            [GameAt(1, Now.AddMinutes(10)), GameAt(2, Now.AddHours(8))], [Hitter(10)]);

        Assert.Equal([1], monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_ConsumesOneMilestonePerCycle()
    {
        // Start in 25 min: still outside the 15-min active window, and all three
        // milestone thresholds (2h, 1h, 30m) have been crossed.
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddMinutes(25))], [Hitter(10)]);

        Assert.Equal([1], monitor.SelectGamesToPoll());   // 2h
        Assert.Equal([1], monitor.SelectGamesToPoll());   // 1h
        Assert.Equal([1], monitor.SelectGamesToPoll());   // 30m
        Assert.Empty(monitor.SelectGamesToPoll());        // all consumed
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_PollsActiveGamesEveryCycle()
    {
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddMinutes(10))], [Hitter(10)]);

        Assert.Equal([1], monitor.SelectGamesToPoll());
        Assert.Equal([1], monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }

    [Fact]
    public void SelectGamesToPoll_IsEmptyForDistantGames()
    {
        var (monitor, _, _) = Create();
        monitor.TrackGames([GameAt(1, Now.AddHours(8))], [Hitter(10)]);

        Assert.Empty(monitor.SelectGamesToPoll());
        monitor.StopMonitoring();
    }
}
