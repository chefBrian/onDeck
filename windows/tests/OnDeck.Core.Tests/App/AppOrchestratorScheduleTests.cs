namespace OnDeck.Core.Tests.App;

public class AppOrchestratorScheduleTests
{
    [Fact]
    public void PreGameRefresh_ResyncsFifteenMinutesBeforeTheFirstGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(20)));

        harness.RunStarted(async app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromMinutes(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_DoesNotFireEarly()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(20)));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(4));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_IsSkippedWhenTheFirstGameAlreadyStarted()
    {
        // The infinite-restart gotcha: resyncing after start cancels in-flight requests and
        // reschedules itself forever.
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddMinutes(-5)));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromHours(1));
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void PreGameRefresh_UsesTheEarliestGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(201, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(4)))
            .AddGame(OrchestratorHarness.GameOf(
                2, OrchestratorHarness.Now.AddMinutes(20), home: "Boston Red Sox",
                away: "New York Yankees", homeTeamId: 111, awayTeamId: 147));

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromMinutes(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void DailyRefresh_FiresAtTheNextEightAm()
    {
        // 14:00 UTC start - the next 8 AM is 18 hours out. No games, so nothing else resyncs.
        var harness = new OrchestratorHarness().AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromHours(18));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void DailyRefresh_FiresTheSameMorningWhenStartedBeforeEight()
    {
        var harness = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero))
            .AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            harness.Time.Advance(TimeSpan.FromHours(5));
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void TimeUntilNextEightAm_IsLocalAndNeverInThePast()
    {
        var beforeEight = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero));
        beforeEight.Run(app =>
        {
            Assert.Equal(TimeSpan.FromHours(5), app.TimeUntilNextEightAm());
            return Task.CompletedTask;
        });

        var afterEight = new OrchestratorHarness(new DateTimeOffset(2026, 8, 8, 9, 30, 0, TimeSpan.Zero));
        afterEight.Run(app =>
        {
            Assert.Equal(TimeSpan.FromHours(22.5), app.TimeUntilNextEightAm());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_InvalidatesTimecodesAndResyncs()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(5)));

        harness.RunStarted(async app =>
        {
            var feed = harness.SeedFeed(1, seeded => seeded.TimeStamp = "20260808_140000");

            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Null(feed.TimeStamp);
            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_DebouncesWithinThirtySeconds()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, OrchestratorHarness.Now.AddHours(5)));

        harness.RunStarted(async app =>
        {
            await app.HandleSystemResumeAsync();
            harness.Time.Advance(TimeSpan.FromSeconds(20));
            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));

            harness.Time.Advance(TimeSpan.FromSeconds(31));
            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(3, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void HandleSystemResumeAsync_DoesNothingWithoutARosterUrlOrTeam()
    {
        var harness = new OrchestratorHarness().AddPlayer(101, "Mookie Betts");

        harness.RunStarted(async app =>
        {
            var before = harness.Http.CountRequests("fxpa/req");
            harness.Settings.RosterUrl = "";

            await app.HandleSystemResumeAsync();
            await SingleThreadedContext.Settle();

            Assert.Equal(before, harness.Http.CountRequests("fxpa/req"));
        });
    }
}
