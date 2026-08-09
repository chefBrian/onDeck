using OnDeck.App.Notifications;

namespace OnDeck.App.Tests;

public class ToastPlannerTests
{
    private static readonly Uri Stream = new("https://www.mlb.com/tv/g776543");
    private static readonly Uri Fantrax = new("https://www.fantrax.com/fantasy/league/lg1/home");

    private static ToastPlanner Planner(RecordingSettingsStore? store = null) =>
        new(store ?? new RecordingSettingsStore());

    [Fact]
    public void BattingReadsLikeTheMacNotification()
    {
        var plan = Planner().Batting("Mookie Betts", 605141, 776543, "SF 1 - LAD 2", "Bot 3", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Mookie Betts is batting", plan!.Title);
        Assert.Equal("SF 1 - LAD 2, Bot 3", plan.Body);
        Assert.Equal("batting-776543-605141", plan.Tag);
        Assert.Null(plan.Group);
        Assert.Equal(Stream, plan.ClickUrl);
        Assert.Equal(605141, plan.PlayerId);
        Assert.Null(plan.Expiry);
    }

    [Fact]
    public void PitchingReadsLikeTheMacNotification()
    {
        var plan = Planner().Pitching("Logan Webb", 657277, 776543, "SF 1 - LAD 2", "Top 4", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Logan Webb is taking the mound", plan!.Title);
        Assert.Equal("SF 1 - LAD 2, Top 4", plan.Body);
        Assert.Equal("pitching-776543-657277", plan.Tag);
        Assert.Null(plan.Group);
        Assert.Null(plan.Expiry);
    }

    [Fact]
    public void NotInLineupCarriesAGameScopedGroup()
    {
        var plan = Planner().NotInLineup("Mookie Betts", 605141, 776543, "SF @ LAD", Fantrax);

        Assert.NotNull(plan);
        Assert.Equal("Mookie Betts is not in the lineup", plan!.Title);
        Assert.Equal("SF @ LAD", plan.Body);
        Assert.Equal("notInLineup-776543-605141", plan.Tag);

        // History.Remove is exact-match, so the Mac's id-prefix sweep becomes a group.
        Assert.Equal("notInLineup-776543", plan.Group);
        Assert.Equal(Fantrax, plan.ClickUrl);
    }

    [Fact]
    public void TheGroupIsThePrefixOfEveryTagInIt()
    {
        // If these ever drift apart, RemoveGroup silently stops matching and stale
        // not-in-lineup toasts survive first pitch.
        var plan = Planner().NotInLineup("Any Player", 12, 776543, "SF @ LAD", null);

        Assert.StartsWith(plan!.Group! + "-", plan.Tag);
    }

    [Fact]
    public void ResultsAreTitledWithJustThePlayerAndSelfExpire()
    {
        var atBat = Planner().AtBatResult("Mookie Betts", 605141, "Home run to left field", Stream);

        Assert.NotNull(atBat);
        Assert.Equal("Mookie Betts", atBat!.Title);
        Assert.Equal("Home run to left field", atBat.Body);
        Assert.Equal(TimeSpan.FromSeconds(30), atBat.Expiry);

        // No stable tag: two at-bats in a row must not overwrite each other.
        Assert.Null(atBat.Tag);
        Assert.Null(atBat.Group);
    }

    [Fact]
    public void PitchingResultsMatchAtBatResults()
    {
        var plan = Planner().PitchingResult(
            "Logan Webb", 657277, "Logan Webb has been pulled from the game", Stream);

        Assert.NotNull(plan);
        Assert.Equal("Logan Webb", plan!.Title);
        Assert.Equal("Logan Webb has been pulled from the game", plan.Body);
        Assert.Equal(TimeSpan.FromSeconds(30), plan.Expiry);
        Assert.Null(plan.Tag);
    }

    [Fact]
    public void EveryTypeCarriesThePlayerIdForItsHeadshot()
    {
        var planner = Planner();

        Assert.Equal(1, planner.Batting("A", 1, 9, "g", "i", null)!.PlayerId);
        Assert.Equal(2, planner.Pitching("B", 2, 9, "g", "i", null)!.PlayerId);
        Assert.Equal(3, planner.AtBatResult("C", 3, "d", null)!.PlayerId);
        Assert.Equal(4, planner.PitchingResult("D", 4, "d", null)!.PlayerId);
        Assert.Equal(5, planner.NotInLineup("E", 5, 9, "g", null)!.PlayerId);
    }

    [Fact]
    public void EachToggleSuppressesItsOwnTypeAndNoOther()
    {
        // Five near-identical guards: this is where a copy-paste slip makes one checkbox
        // silence the wrong alert.
        Assert.Null(Planner(new RecordingSettingsStore { NotifyBatting = false })
            .Batting("A", 1, 9, "g", "i", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyBatting = false })
            .Pitching("A", 1, 9, "g", "i", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyPitching = false })
            .Pitching("A", 1, 9, "g", "i", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyPitching = false })
            .Batting("A", 1, 9, "g", "i", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyAtBatResult = false })
            .AtBatResult("A", 1, "d", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyAtBatResult = false })
            .PitchingResult("A", 1, "d", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyPitchingResult = false })
            .PitchingResult("A", 1, "d", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyPitchingResult = false })
            .AtBatResult("A", 1, "d", null));

        Assert.Null(Planner(new RecordingSettingsStore { NotifyNotInLineup = false })
            .NotInLineup("A", 1, 9, "g", null));
        Assert.NotNull(Planner(new RecordingSettingsStore { NotifyNotInLineup = false })
            .Batting("A", 1, 9, "g", "i", null));
    }

    [Fact]
    public void TheTogglesAreReadAtSendTimeNotAtConstruction()
    {
        // The Settings window writes straight through to the store while the app runs.
        var store = new RecordingSettingsStore();
        var planner = new ToastPlanner(store);

        Assert.NotNull(planner.Batting("A", 1, 9, "g", "i", null));

        store.NotifyBatting = false;

        Assert.Null(planner.Batting("A", 1, 9, "g", "i", null));
    }

    [Fact]
    public void IdentifiersMatchTheDocumentedFormat()
    {
        Assert.Equal("batting-776543-605141", ToastIds.Batting(776543, 605141));
        Assert.Equal("pitching-776543-605141", ToastIds.Pitching(776543, 605141));
        Assert.Equal("notInLineup-776543-605141", ToastIds.NotInLineup(776543, 605141));
        Assert.Equal("notInLineup-776543", ToastIds.NotInLineupGroup(776543));
    }
}
