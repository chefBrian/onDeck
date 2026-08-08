using System.Net;
using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorSyncTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

    [Fact]
    public void StartAsync_SyncsTheRosterThenFetchesTheSchedule()
    {
        var harness = Harness();

        harness.RunStarted(app =>
        {
            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
            Assert.Equal(1, harness.Http.CountRequests("/v1/schedule"));
            Assert.NotNull(harness.Roster.LastSyncDate);
            Assert.Equal(1, app.LoadedPlayerCount);
            Assert.Null(app.SyncError);
            Assert.False(app.IsSyncing);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_StartsMonitoringAndSeedsLineupsAfterwards()
    {
        // Seeding must happen AFTER StartMonitoring - it calls StopMonitoring internally,
        // which clears LineupPlayerIds.
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(_ =>
        {
            Assert.True(harness.Monitor.IsMonitoring);
            Assert.Equal([101, 102, 103], harness.Monitor.LineupPlayerIds[1].Home.Order());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_SeedsProbablePitchersEvenWithoutABattingCard()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(_ =>
        {
            Assert.Equal([901], harness.Monitor.LineupPlayerIds[1].HomePitchers.Order());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_PurgesEveryNotificationBeforeRefreshing()
    {
        var harness = Harness();

        harness.RunStarted(_ =>
        {
            Assert.Contains("purgeAll", harness.Sink.Calls);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_IsIdempotent()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Equal(1, harness.Http.CountRequests("fxpa/req"));
        });
    }

    [Fact]
    public void StartAsync_DoesNothingWithoutARosterUrl()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = "";

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void StartAsync_FetchesTeamsWhenTheUrlHasNoTeamAndNoneIsSelected()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = null;

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Equal(["t1", "t2"], app.AvailableTeams.Select(team => team.Id));
            Assert.False(app.IsLoadingTeams);
            Assert.Equal(0, harness.Http.CountRequests("/v1/schedule"));
        });
    }

    [Fact]
    public void StartAsync_UsesTheSelectedTeamWhenTheUrlHasNone()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "t2";

            await app.StartAsync(harness.Lifetime.Token);
            await SingleThreadedContext.Settle();

            Assert.Contains("\"teamId\":\"t2\"", harness.Http.RequestBodies[0].Replace(" ", ""));
            Assert.Equal(1, harness.Http.CountRequests("/v1/schedule"));
        });
    }

    [Fact]
    public void StartAsync_MarksPlayersWithoutAGameAsDayOff()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(202, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[101]);
            var devers = Assert.IsType<PlayerState.Inactive>(harness.States.PlayerStates[202]);
            Assert.IsType<PlayerState.InactiveReason.DayOff>(devers.Reason);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_MarksStartingPitchersWhoArentTodaysProbableAsDayOff()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddPlayer(902, "Tyler Glasnow", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[901]);
            var glasnow = Assert.IsType<PlayerState.Inactive>(harness.States.PlayerStates[902]);
            Assert.IsType<PlayerState.InactiveReason.DayOff>(glasnow.Reason);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void StartAsync_LeavesRelieversUpcoming()
    {
        // isStartingPitcherOnly is SP-and-not-RP; a swingman stays in the pool.
        var harness = new OrchestratorHarness()
            .AddPlayer(903, "Michael Kopech", positions: "SP,RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(_ =>
        {
            Assert.IsType<PlayerState.Upcoming>(harness.States.PlayerStates[903]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void FetchTeamsAsync_ReportsAnInvalidUrl()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = "not a url";

            await app.FetchTeamsAsync();

            Assert.Equal("Invalid Fantrax URL", app.TeamsError);
            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void FetchTeamsAsync_ClearsASelectionThatNoLongerExists()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "gone";

            await app.FetchTeamsAsync();

            Assert.Equal("", harness.Settings.SelectedTeamId);
            Assert.Null(app.TeamsError);
        });
    }

    [Fact]
    public void FetchTeamsAsync_KeepsASelectionThatStillExists()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = "t2";

            await app.FetchTeamsAsync();

            Assert.Equal("t2", harness.Settings.SelectedTeamId);
        });
    }

    [Fact]
    public void FetchTeamsAsync_RecordsErrorsAndStopsLoading()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Http.MapStatus("fantrax.com/fxpa/req", HttpStatusCode.InternalServerError);

            await app.FetchTeamsAsync();

            Assert.StartsWith("Couldn't load teams:", app.TeamsError);
            Assert.False(app.IsLoadingTeams);
            Assert.Empty(app.AvailableTeams);
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsFalseWithoutALeagueOrTeam()
    {
        var harness = Harness();

        harness.Run(async app =>
        {
            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;
            harness.Settings.SelectedTeamId = null;

            Assert.False(await app.ResyncRosterAsync());
            Assert.Empty(harness.Http.Requests);
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsTrueAndRefetchesTheSchedule()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            Assert.True(await app.ResyncRosterAsync());
            await SingleThreadedContext.Settle();

            Assert.Equal(2, harness.Http.CountRequests("fxpa/req"));
            Assert.Equal(2, harness.Http.CountRequests("/v1/schedule"));
            Assert.Equal(2, harness.Sink.Calls.Count(call => call == "purgeAll"));
        });
    }

    [Fact]
    public void ResyncRosterAsync_ReturnsFalseWhenTheRosterSyncFails()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Http.MapStatus("fantrax.com/fxpa/req", HttpStatusCode.InternalServerError);

            Assert.False(await app.ResyncRosterAsync());
            Assert.StartsWith("Roster sync failed:", app.SyncError);
        });
    }

    [Fact]
    public void EffectiveTeamId_PrefersTheUrlOverThePicker()
    {
        var harness = Harness();

        harness.Run(app =>
        {
            harness.Settings.SelectedTeamId = "t2";

            Assert.Equal("lg1", app.ParsedLeagueId);
            Assert.True(app.UrlHasTeamId);
            Assert.Equal("t1", app.EffectiveTeamId);

            harness.Settings.RosterUrl = OrchestratorHarness.LeagueUrlWithoutTeam;

            Assert.False(app.UrlHasTeamId);
            Assert.Equal("t2", app.EffectiveTeamId);
            return Task.CompletedTask;
        });
    }
}
