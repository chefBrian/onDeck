using System.Net;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using static OnDeck.Core.Tests.Managers.GameMonitorLifecycleTests;

namespace OnDeck.Core.Tests.Managers;

public class GameMonitorPollingTests
{
    // $$$ / {{{ }}} so the JSON's doubled closing braces stay literal.
    private static string FeedWith(
        string gameState, string detailedState, string timeStamp = "20260808_140000") => $$$"""
    {
      "metaData": {"timeStamp": "{{{timeStamp}}}"},
      "gameData": {
        "status": {"abstractGameState": "{{{gameState}}}", "detailedState": "{{{detailedState}}}"},
        "teams": {"away": {"id": 137, "name": "San Francisco Giants"},
                  "home": {"id": 119, "name": "Los Angeles Dodgers"}}
      },
      "liveData": {}
    }
    """;

    [Fact]
    public async Task PollSingleGameAsync_FullFetchesWhenThereIsNoSeed()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));

        await monitor.PollSingleGameAsync(1, GameAt(1, Now), CancellationToken.None);

        Assert.Contains("/feed/live", handler.LastUri!.AbsoluteUri);
        Assert.DoesNotContain("diffPatch", handler.LastUri.AbsoluteUri);
        Assert.Equal("20260808_140000", monitor.LatestFeeds[1].TimeStamp);
    }

    [Fact]
    public async Task PollSingleGameAsync_UsesDiffPatchOnceSeeded()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueJson(
            """[{"diff": [{"op": "replace", "path": "/liveData/linescore/teams/home/runs", "value": 4}]}]""");
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Contains("diffPatch?startTimecode=20260808_140000", handler.LastUri!.AbsoluteUri);
        Assert.Equal(4, monitor.LatestFeeds[1].HomeScore);
    }

    [Fact]
    public async Task PollSingleGameAsync_NoChangesLeavesTheFeedAlone()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);
        var before = monitor.LatestFeeds[1].Clone();

        handler.EnqueueJson("[]");
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Equal(before, monitor.LatestFeeds[1]);
    }

    [Fact]
    public async Task PollSingleGameAsync_FullUpdateReplacesTheFeed()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueJson(FeedWith("Live", "In Progress", timeStamp: "20260808_141000"));
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Equal("20260808_141000", monitor.LatestFeeds[1].TimeStamp);
    }

    [Fact]
    public async Task PollSingleGameAsync_NullsTimestampOnTransientError()
    {
        var (monitor, _, handler) = Create();
        handler.EnqueueJson(FeedWith("Live", "In Progress"));
        var game = GameAt(1, Now);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        await monitor.PollSingleGameAsync(1, game, CancellationToken.None);

        Assert.Null(monitor.LatestFeeds[1].TimeStamp);
        Assert.Equal("Live", monitor.LatestFeeds[1].GameState);   // rest preserved for the UI
    }

    [Fact]
    public async Task PollSingleGameAsync_FinalMarksRosterPlayersGameOverAndStops()
    {
        var (monitor, _, handler) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.TrackGames([GameAt(1, Now)], [Hitter(10), Hitter(11, "Boston Red Sox")]);

        handler.EnqueueJson(FeedWith("Final", "Final"));
        await monitor.PollSingleGameAsync(1, GameAt(1, Now), CancellationToken.None);

        var inactive = Assert.IsType<PlayerState.Inactive>(states.PlayerStates[10]);
        Assert.Equal(1, Assert.IsType<PlayerState.InactiveReason.GameOver>(inactive.Reason).GamePk);
        Assert.False(states.PlayerStates.ContainsKey(11));   // not in this game
        Assert.False(monitor.IsMonitoring);
    }

    [Fact]
    public async Task PollSingleGameAsync_PostponedStopsPollingWithoutMarkingPlayers()
    {
        // Postponed carries Final with no stats; marking players gameOver would filter them
        // out of the UI entirely, so they stay .upcoming and keep the PPD label.
        var (monitor, _, handler) = Create();
        var states = new StateManager();
        monitor.Configure(states);
        monitor.TrackGames([GameAt(1, Now)], [Hitter(10)]);

        handler.EnqueueJson(FeedWith("Final", "Postponed"));
        await monitor.PollSingleGameAsync(1, GameAt(1, Now), CancellationToken.None);

        Assert.Empty(states.PlayerStates);
        Assert.False(monitor.IsMonitoring);
        Assert.True(monitor.LatestFeeds.ContainsKey(1));   // feed retained
    }
}
