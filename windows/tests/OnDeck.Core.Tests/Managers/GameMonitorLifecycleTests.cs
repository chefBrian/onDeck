using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorLifecycleTests
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    internal static Game GameAt(int id, DateTimeOffset start, string home = "Los Angeles Dodgers",
        string away = "San Francisco Giants", int? homePitcher = null, int? awayPitcher = null) =>
        new(id, home, away, 119, 137, start, homePitcher, awayPitcher, [], [], []);

    internal static Player Hitter(int id, string team = "Los Angeles Dodgers") =>
        new(id, $"Player {id}", team,
            new HashSet<PlayerPosition> { PlayerPosition.Hitter },
            new HashSet<string> { "OF" },
            RosterStatus.Active);

    internal static (GameMonitor Monitor, FakeTimeProvider Time, StubHttpMessageHandler Handler) Create()
    {
        var handler = new StubHttpMessageHandler();
        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return (new GameMonitor(new MlbStatsApi(handler.CreateClient(), time), time), time, handler);
    }

    [Fact]
    public void StartMonitoring_SetsIsMonitoring()
    {
        var (monitor, _, _) = Create();

        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.True(monitor.IsMonitoring);
        monitor.StopMonitoring();
    }

    [Fact]
    public void StartMonitoring_ClearsSeedDataSetBeforehand()
    {
        // The seed-after-start rule: StartMonitoring calls StopMonitoring internally.
        var (monitor, _, _) = Create();
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.Empty(monitor.LineupPlayerIds);
        monitor.StopMonitoring();
    }

    [Fact]
    public void StopMonitoring_ClearsEverythingIncludingFeeds()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);
        monitor.LatestFeeds[1] = new LiveFeedData { GameState = "Live" };
        monitor.LastPlayDescriptions[10] = "single";
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StopMonitoring();

        Assert.False(monitor.IsMonitoring);
        Assert.Empty(monitor.LatestFeeds);
        Assert.Empty(monitor.LastPlayDescriptions);
        Assert.Empty(monitor.LineupPlayerIds);
    }

    [Fact]
    public void StopMonitoringGame_RetainsThatGamesFeed()
    {
        // The Done section reads feed.PlayerStats for finished games.
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5)), GameAt(2, Now.AddHours(6))], [Hitter(10)]);
        monitor.LatestFeeds[1] = new LiveFeedData { GameState = "Final" };
        monitor.LineupPlayerIds[1] = new GameLineup { Home = [10] };

        monitor.StopMonitoring(1);

        Assert.True(monitor.LatestFeeds.ContainsKey(1));
        Assert.False(monitor.LineupPlayerIds.ContainsKey(1));
        Assert.True(monitor.IsMonitoring);      // game 2 is still monitored
        monitor.StopMonitoring();
    }

    [Fact]
    public void StopMonitoringGame_StopsAltogetherWhenNoGamesRemain()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        monitor.StopMonitoring(1);

        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public void InvalidateTimecodes_NullsTimestampsButKeepsTheRest()
    {
        var (monitor, _, _) = Create();
        monitor.LatestFeeds[1] = new LiveFeedData
        {
            TimeStamp = "20260808_140000",
            GameState = "Live",
            HomeScore = 3,
        };

        monitor.InvalidateTimecodes();

        Assert.Null(monitor.LatestFeeds[1].TimeStamp);
        Assert.Equal("Live", monitor.LatestFeeds[1].GameState);
        Assert.Equal(3, monitor.LatestFeeds[1].HomeScore);
    }

    [Fact]
    public void IsLive_IsFalseUntilTheFeedReportsInProgress()
    {
        var (monitor, _, _) = Create();
        monitor.StartMonitoring([GameAt(1, Now.AddHours(5))], [Hitter(10)]);

        Assert.False(monitor.IsLive(1));
        monitor.StopMonitoring();
    }
}
