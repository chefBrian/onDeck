using OnDeck.Core.Models;

namespace OnDeck.Core.Tests.App;

public class AppOrchestratorTransitionTests
{
    private static readonly DateTimeOffset FirstPitch = OrchestratorHarness.Now.AddHours(5);

    private const string GameString = "Giants 1 - Dodgers 2";

    private static PlayerState.GameContext Context(
        PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting, int gamePk = 1) =>
        new(gamePk, role, "Bot 3", "Dodgers", "Giants", 119, 137, 2, 1, 1, 2, 1, false, false, false);

    private static PlayerState Active(PlayerState.ActiveRole role = PlayerState.ActiveRole.Batting) =>
        new PlayerState.Active(Context(role));

    private static OrchestratorHarness Harness() =>
        new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddPlayer(901, "Blake Snell", positions: "SP")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch, exclusiveCallSign: "Peacock"));

    [Fact]
    public void Transition_ToBattingNotifiesWithTheGameStringInningAndStreamLink()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Equal(
                $"batting:101:1:{GameString}:Bot 3:https://www.peacocktv.com/sports/mlb",
                Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("batting:", StringComparison.Ordinal))));
        });
    }

    [Fact]
    public void Transition_ToPitchingNotifies()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            Assert.Equal(
                $"pitching:901:1:{GameString}:Bot 3:https://www.peacocktv.com/sports/mlb",
                Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("pitching:", StringComparison.Ordinal))));
        });
    }

    [Fact]
    public void Transition_ActiveToActiveDoesNotResend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            // Same role, updated count/score - the Mac only notifies on entering active.
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Single(harness.Sink.Calls.Where(call => call.StartsWith("batting:", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void Transition_PurgesBattingWhenTheStateChangesDuringTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () =>
            {
                harness.Sink.DuringNotify = null;       // only interfere with the first send
                await Task.Yield();
                harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            };

            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_PurgesPitchingWhenTheStateChangesDuringTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () =>
            {
                harness.Sink.DuringNotify = null;
                await Task.Yield();
                harness.States.Update(901, new PlayerState.Upcoming(FirstPitch));
            };

            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_KeepsTheNotificationWhenTheStateHoldsThroughTheSend()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.Sink.DuringNotify = async () => await Task.Yield();

            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain("purgeBatting:101:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_OutOfBattingPurgesAndReportsTheAtBatResult()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();
            harness.Monitor.LastPlayDescriptions[101] = "Mookie Betts doubles (12) on a line drive";

            harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.Contains(
                "atBatResult:101:Mookie Betts doubles (12) on a line drive", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_OutOfBattingWithoutADescriptionOnlyPurges()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            harness.States.Update(101, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("atBatResult:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_OutOfPitchingOnlyPurges()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();
            harness.Monitor.LastPlayDescriptions[901] = "strikeout swinging";

            harness.States.Update(901, new PlayerState.Upcoming(FirstPitch));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("atBatResult:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_PitcherPulledPurgesAndReportsTheResult()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            harness.States.Update(
                901, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            await SingleThreadedContext.Settle();

            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
            Assert.Contains(
                "pitchingResult:901:Blake Snell has been pulled from the game", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_HitterSubstitutedSendsNothing()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();
            var callsBefore = harness.Sink.Calls.Count;

            harness.States.Update(
                101, new PlayerState.Inactive(new PlayerState.InactiveReason.Substituted(1)));
            await SingleThreadedContext.Settle();

            Assert.Equal(callsBefore, harness.Sink.Calls.Count);
        });
    }

    [Fact]
    public void Transition_GameOverPurgesBothRoles()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            harness.States.Update(901, Active(PlayerState.ActiveRole.Pitching));
            await SingleThreadedContext.Settle();

            harness.States.SetGameOver([101, 901], 1);
            await SingleThreadedContext.Settle();

            Assert.Contains("purgeBatting:101:1", harness.Sink.Calls);
            Assert.Contains("purgePitching:901:1", harness.Sink.Calls);
        });
    }

    [Fact]
    public void Transition_IgnoresUnavailablePlayers()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(198, "On The IL", statusId: 3)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.States.Update(198, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_IgnoresBenchPlayersWhenTheyAreHidden()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(199, "On The Bench", statusId: 2)
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.Settings.HideBenchPlayers = true;

        harness.RunStarted(async app =>
        {
            harness.States.Update(199, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_IgnoresPlayersWhoAreNotOnTheRoster()
    {
        var harness = Harness();

        harness.RunStarted(async app =>
        {
            harness.States.Update(555, Active());
            await SingleThreadedContext.Settle();

            Assert.DoesNotContain(harness.Sink.Calls, call => call.StartsWith("batting:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Transition_FallsBackToTheMlbTvLinkWithoutAnExclusiveBroadcast()
    {
        var harness = new OrchestratorHarness()
            .AddPlayer(101, "Mookie Betts")
            .AddGame(OrchestratorHarness.GameOf(1, FirstPitch));

        harness.RunStarted(async app =>
        {
            harness.States.Update(101, Active());
            await SingleThreadedContext.Settle();

            Assert.EndsWith("https://www.mlb.com/tv/g1", harness.Sink.Calls.First(
                call => call.StartsWith("batting:", StringComparison.Ordinal)));
        });
    }
}
