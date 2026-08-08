using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorListTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private static PlayerState.GameContext Context(
        int gamePk = 1, PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting) =>
        new(gamePk, role, "Bot 3", "Dodgers", "Giants", 119, 137, 2, 1, 1, 2, 1, false, false, false);

    [Fact]
    public void UpcomingPlayers_SortsByStartTimeThenName()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Freddie Freeman")
            .AddPlayer(201, "Rafael Devers", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch.AddHours(1)))
            .AddGame(OrchestratorHarness.GameOf(
                2, FirstPitch, home: "Boston Red Sox", away: "New York Yankees",
                homeTeamId: 111, awayTeamId: 147));

        harness.RunStarted(app =>
        {
            Assert.Equal(
                ["Rafael Devers", "Freddie Freeman", "Mookie Betts"],
                app.UpcomingPlayers.Select(display => display.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void UpcomingPlayers_CarryTheirLineupBadgeAndStartTime()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [103, 101, 102]));

        harness.RunStarted(app =>
        {
            var row = Assert.Single(app.UpcomingPlayers);
            Assert.Equal(LineupInfoKind.BattingOrder, row.Lineup.Kind);
            Assert.Equal(2, row.Lineup.Spot);
            Assert.Equal(FirstPitch, row.StartTime);
            Assert.Equal(1, row.GamePk);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Lists_SkipUnavailablePlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Injured Guy", statusId: 3)
            .AddPlayer(103, "Minors Guy", statusId: 9)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Equal(["Mookie Betts"], app.UpcomingPlayers.Select(display => display.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void SettingsChanged_RefiltersBenchPlayersWithoutAnyNetworkCall()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(102, "Bench Guy", statusId: 2)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Equal(2, app.UpcomingPlayers.Count);
            var requestsBefore = harness.Http.Requests.Count;

            harness.Settings.HideBenchPlayers = true;
            app.SettingsChanged();

            Assert.Equal(["Mookie Betts"], app.UpcomingPlayers.Select(display => display.Name));
            Assert.Equal(requestsBefore, harness.Http.Requests.Count);

            harness.Settings.HideBenchPlayers = false;
            app.SettingsChanged();

            Assert.Equal(2, app.UpcomingPlayers.Count);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void InGamePlayers_HoldUpcomingPlayersWhoseGameIsLive()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            await SingleThreadedContext.Settle();

            Assert.Empty(app.UpcomingPlayers);
            var row = Assert.Single(app.InGamePlayers);
            Assert.Equal("Mookie Betts", row.Name);
            Assert.False(row.IsActive);
        });
    }

    [Fact]
    public void InGamePlayers_SortByProximityThenDelayThenExclusion()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "At Bat")
            .AddPlayer(102, "On Deck")
            .AddPlayer(105, "Deep In Order")
            .AddPlayer(901, "Bullpen Arm", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            harness.SeedFeed(1, feed =>
            {
                feed.InningHalf = "Bottom";
                feed.InningState = "Bottom";
                feed.CurrentBatterId = 101;
                feed.HomeBattingOrder = [101, 102, 103, 104, 105];
            });

            app.SettingsChanged();      // local rebuild against the seeded feed
            await SingleThreadedContext.Settle();

            Assert.Equal(
                ["At Bat", "On Deck", "Deep In Order", "Bullpen Arm"],
                app.InGamePlayers.Select(display => display.Name));
        });
    }

    [Fact]
    public void InGamePlayers_PushExcludedHittersToTheBottom()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "In The Card")
            .AddPlayer(199, "Benched")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeLineup: [101, 102, 103]));

        harness.RunStarted(async app =>
        {
            harness.GoLive(1);
            app.SettingsChanged();
            await SingleThreadedContext.Settle();

            Assert.Equal(["In The Card", "Benched"], app.InGamePlayers.Select(display => display.Name));
            Assert.False(app.InGamePlayers[1].IsInLineup);
            Assert.Equal("Not in Lineup", app.InGamePlayers[1].StatLine);
        });
    }

    [Fact]
    public void ActivePlayers_HoldPlayersInTheActiveStateAndDriveTheTrayFlag()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            Assert.False(app.HasActivePlayers);

            harness.States.Update(101, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();

            var row = Assert.Single(app.ActivePlayers);
            Assert.True(row.IsActive);
            Assert.True(app.HasActivePlayers);
            Assert.Empty(app.InGamePlayers);
        });
    }

    [Fact]
    public void MenuBarTitleText_JoinsUpToThreeNamesThenCounts()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "A")
            .AddPlayer(102, "B")
            .AddPlayer(103, "C")
            .AddPlayer(104, "D")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            Assert.Equal("", app.MenuBarTitleText);

            harness.States.Update(101, new PlayerState.Active(Context()));
            harness.States.Update(102, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();
            Assert.Equal("A | B", app.MenuBarTitleText);

            harness.States.Update(103, new PlayerState.Active(Context()));
            harness.States.Update(104, new PlayerState.Active(Context()));
            await SingleThreadedContext.Settle();
            Assert.Equal("A | B | C +1", app.MenuBarTitleText);
        });
    }

    [Fact]
    public void DonePlayers_NeedAStatLineMatchingTheirRole()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Hit A Double")
            .AddPlayer(102, "Never Played")
            .AddPlayer(901, "Relief Pitcher", positions: "RP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed =>
            {
                feed.PlayerStats[101] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 4, Hits = 2, Doubles = 1 },
                };

                // A pitcher-only player's batting line must not qualify them.
                feed.PlayerStats[901] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 1, Hits = 1 },
                };
            });

            harness.States.SetGameOver([101, 102, 901], 1);
            await SingleThreadedContext.Settle();

            var row = Assert.Single(app.DonePlayers);
            Assert.Equal("Hit A Double", row.Name);
            Assert.Equal("2-4 · 2B", row.StatLine);
        });
    }

    [Fact]
    public void DonePlayers_IncludeSubstitutedPitchersAndSortHittersFirst()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Hitter")
            .AddPlayer(901, "Starter", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, homeProbablePitcher: 901));

        harness.RunStarted(async app =>
        {
            harness.SeedFeed(1, feed =>
            {
                feed.PlayerStats[101] = new PlayerGameStats
                {
                    Batting = new PlayerBattingStats { AtBats = 3, Hits = 1 },
                };
                feed.PlayerStats[901] = new PlayerGameStats
                {
                    Pitching = new PlayerPitchingStats { InningsPitched = "6.0", StrikeOuts = 8, EarnedRuns = 2 },
                };
            });

            harness.States.Update(
                901, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            harness.States.SetGameOver([101], 1);
            await SingleThreadedContext.Settle();

            Assert.Equal(["Hitter", "Starter"], app.DonePlayers.Select(display => display.Name));
            Assert.Equal("6.0 IP, 8K, 2ER", app.DonePlayers[1].StatLine);
        });
    }

    [Fact]
    public void DonePlayers_ExcludeDayOffPlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(201, "Off Today", fantraxTeam: "BOS")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(app =>
        {
            Assert.Empty(app.DonePlayers);
            Assert.Empty(app.UpcomingPlayers);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Rows_CarryTheStreamLinkForTheirGame()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, exclusiveCallSign: "Peacock"));

        harness.RunStarted(app =>
        {
            Assert.Equal(
                new Uri("https://www.peacocktv.com/sports/mlb"),
                Assert.Single(app.UpcomingPlayers).StreamUrl);
            return Task.CompletedTask;
        });
    }
}
