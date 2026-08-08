using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorLineupTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private const string Matchup = "San Francisco Giants @ Los Angeles Dodgers";

    /// <summary>Card filed for the Dodgers without 199.</summary>
    private static OrchestratorHarness FiledWithout199() =>
        new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

    [Fact]
    public void Reconcile_NotifiesActiveHittersMissingFromTheFiledCard()
    {
        var harness = FiledWithout199();

        harness.RunStarted(_ =>
        {
            Assert.Equal(
                [$"notInLineup:199:1:{Matchup}:{OrchestratorHarness.LeagueUrl}"],
                harness.Sink.Calls.Where(call => call.StartsWith("notInLineup:", StringComparison.Ordinal)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_NotifiesOnlyOncePerPlayer()
    {
        var harness = FiledWithout199();

        harness.RunStarted(async app =>
        {
            // A later feed changes the card, firing OnLineupUpdate again.
            harness.Monitor.ProcessFeed(
                new LiveFeedData
                {
                    GameState = "Preview",
                    DetailedState = "Pre-Game",
                    HomeBattingOrder = [101, 102, 103, 104],
                },
                1,
                harness.Schedule.TodaysGames[0]);

            await SingleThreadedContext.Settle();

            Assert.Single(
                harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Reconcile_SkipsPlayersWhoAreOnTheCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "In The Card")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsPlayersWhoArentOnTheActiveRoster()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "On The Bench", statusId: 2)
            .AddPlayer(198, "On The IL", statusId: 3)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsPitchers()
    {
        // Relievers are never on the batting card, so its contents say nothing about them.
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Reliever", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsOnlyTheOpponentsCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, awayLineup: [201, 202, 203]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_SkipsOnceTheFeedReportsTheGameLive()
    {
        // Too late to act on a bench swap.
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed => feed.GameState = "Live");

            harness.Monitor.ProcessFeed(
                new LiveFeedData
                {
                    GameState = "Preview",
                    DetailedState = "Pre-Game",
                    HomeBattingOrder = [101, 102, 103],
                },
                1,
                harness.Schedule.TodaysGames[0]);

            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Reconcile_SkipsWhenTheScheduledStartHasPassedAndNoFeedExists()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "Left Out")
            .AddGame(OrchestratorHarness.GameOf(
                1, OrchestratorHarness.Now.AddMinutes(-30), homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("notInLineup:", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Reconcile_StartsOverOnEveryScheduleRefresh()
    {
        var harness = FiledWithout199();

        harness.RunStarted(async app =>
        {
            await app.ResyncRosterAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(
                2,
                harness.Sink.Calls.Count(call => call.StartsWith("notInLineup:", StringComparison.Ordinal)));
        });
    }
}
