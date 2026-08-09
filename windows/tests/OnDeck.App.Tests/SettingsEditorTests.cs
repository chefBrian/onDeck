using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class SettingsEditorTests
{
    [Fact]
    public void AToggleWritesThroughAndAnnouncesTheChange()
    {
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.HideBenchPlayers = true;

        Assert.True(store.HideBenchPlayers);
        Assert.Equal(new[] { "HideBenchPlayers" }, store.Writes);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void EveryNotificationToggleWritesItsOwnKey()
    {
        // Nine near-identical properties: this is where a copy-paste slip points two toggles at
        // one key and nothing complains until a user notices the wrong alerts vanished.
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);

        editor.NotifyBatting = false;
        editor.NotifyPitching = false;
        editor.NotifyAtBatResult = false;
        editor.NotifyPitchingResult = false;
        editor.NotifyNotInLineup = false;

        Assert.False(store.NotifyBatting);
        Assert.False(store.NotifyPitching);
        Assert.False(store.NotifyAtBatResult);
        Assert.False(store.NotifyPitchingResult);
        Assert.False(store.NotifyNotInLineup);
        Assert.Equal(
            new[]
            {
                "NotifyBatting", "NotifyPitching", "NotifyAtBatResult",
                "NotifyPitchingResult", "NotifyNotInLineup",
            },
            store.Writes);
    }

    [Fact]
    public void EachToggleReadsBackFromTheStore()
    {
        var store = new RecordingSettingsStore
        {
            HideBenchPlayers = true,
            AlwaysOpenPopout = true,
            NotifyAtBatResult = false,
        };

        var editor = new SettingsEditor(store);

        Assert.True(editor.HideBenchPlayers);
        Assert.True(editor.AlwaysOpenPopout);
        Assert.False(editor.NotifyAtBatResult);
        Assert.True(editor.NotifyBatting);
    }

    [Fact]
    public void WritingTheValueItAlreadyHasDoesNothing()
    {
        // Changed runs SettingsChanged() -> UpdatePlayerLists() -> StateChanged -> a re-render.
        // A write on every render would loop.
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.NotifyBatting = true;
        editor.HideBenchPlayers = false;
        editor.RosterUrl = "";

        Assert.Empty(store.Writes);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void PropertyChangedNamesThePropertyThatMoved()
    {
        var editor = new SettingsEditor(new RecordingSettingsStore());
        var names = new List<string?>();
        editor.PropertyChanged += (_, e) => names.Add(e.PropertyName);

        editor.AlwaysOpenPopout = true;

        Assert.Equal(new[] { "AlwaysOpenPopout" }, names);
    }

    [Fact]
    public void TheRosterUrlIsTrimmedBeforeItIsStored()
    {
        var store = new RecordingSettingsStore();

        new SettingsEditor(store).RosterUrl =
            "  https://www.fantrax.com/fantasy/league/lg1/home  ";

        Assert.Equal("https://www.fantrax.com/fantasy/league/lg1/home", store.RosterUrl);
    }

    [Fact]
    public void RetypingTheSameUrlWithStrayWhitespaceIsNotAWrite()
    {
        // The window commits on Enter, on focus loss and on close, so the same text arrives
        // more than once by design.
        var store = new RecordingSettingsStore { RosterUrl = "https://x" };
        store.Writes.Clear();
        var editor = new SettingsEditor(store);

        editor.RosterUrl = "  https://x  ";

        Assert.Empty(store.Writes);
    }

    [Fact]
    public void ClearingTheRosterUrlStoresNullRatherThanAnEmptyString()
    {
        var store = new RecordingSettingsStore { RosterUrl = "https://x" };
        var editor = new SettingsEditor(store);

        editor.RosterUrl = "";

        Assert.Null(store.RosterUrl);
        Assert.Equal("", editor.RosterUrl);
    }

    [Fact]
    public void AnUnsetRosterUrlReadsBackAsEmptyText()
    {
        Assert.Equal("", new SettingsEditor(new RecordingSettingsStore()).RosterUrl);
    }

    [Fact]
    public void SelectingThePlaceholderClearsTheStoredTeam()
    {
        // Swift assigns selectedTeamID = "" rather than nil; EffectiveTeamId treats an empty
        // string as no selection.
        var store = new RecordingSettingsStore { SelectedTeamId = "t2" };
        var editor = new SettingsEditor(store);

        editor.SelectedTeamId = "";

        Assert.Equal("", store.SelectedTeamId);
    }

    [Fact]
    public void PickingATeamWritesItThrough()
    {
        var store = new RecordingSettingsStore();
        var editor = new SettingsEditor(store);
        var changes = 0;
        editor.Changed += () => changes++;

        editor.SelectedTeamId = "t2";

        Assert.Equal("t2", store.SelectedTeamId);
        Assert.Equal(1, changes);
    }
}
