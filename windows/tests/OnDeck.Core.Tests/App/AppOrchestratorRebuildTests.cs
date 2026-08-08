using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorRebuildTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static PlayerState.GameContext Context(int gamePk = 1) =>
        new(gamePk, PlayerState.ActiveRole.Batting, "Bot 3", "Dodgers", "Giants",
            119, 137, 2, 1, 1, 2, 1, false, false, false);

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Freddie Freeman")
            .AddPlayer(103, "Will Smith")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

    [Fact]
    public void StateChanges_CollapseIntoOneRebuildPerTick()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            var rebuilds = 0;
            app.StateChanged += () => rebuilds++;

            harness.States.Update(101, new PlayerState.Active(Context()));
            harness.States.Update(102, new PlayerState.Active(Context()));
            harness.States.Update(103, new PlayerState.Active(Context()));
            Assert.Equal(0, rebuilds);          // nothing rebuilt synchronously

            await SingleThreadedContext.Settle();

            Assert.Equal(1, rebuilds);
            Assert.Equal(3, app.ActivePlayers.Count);
        });
    }

    [Fact]
    public void StateChanges_RebuildAgainOnTheNextTick()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            var rebuilds = 0;
            app.StateChanged += () => rebuilds++;

            harness.States.Update(102, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, rebuilds);
            Assert.Equal(2, app.ActivePlayers.Count);
        });
    }

    [Fact]
    public void StateChanged_FiresOnTheCoreContext()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            var pumpThread = Environment.CurrentManagedThreadId;
            var eventThread = 0;
            app.StateChanged += () => eventThread = Environment.CurrentManagedThreadId;

            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            Assert.Equal(pumpThread, eventThread);
        });
    }

    [Fact]
    public void GameStart_RebuildsAndPurgesNotInLineupForThatGame()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            Assert.Equal(3, app.UpcomingPlayers.Count);

            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Empty(app.UpcomingPlayers);
            Assert.Equal(3, app.InGamePlayers.Count);
            Assert.Contains("purgeNotInLineup:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void GameStart_FiresOnlyOnce()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Single(harness.Sink.Calls.Where(call => call == "purgeNotInLineup:1"));
        });
    }
}
