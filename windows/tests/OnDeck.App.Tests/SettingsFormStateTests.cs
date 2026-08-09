using OnDeck.App.Views;
using OnDeck.Core.Networking;

namespace OnDeck.App.Tests;

public class SettingsFormStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 19, 30, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<FantraxTeam> TwoTeams =
    [
        new("t1", "Bronx Bombers"),
        new("t2", "Queens Crew"),
    ];

    /// <summary>A URL that parses to a league but carries no teamId - the picker's whole reason.</summary>
    private static SettingsInput LeagueOnly() => new()
    {
        RosterUrl = "https://www.fantrax.com/fantasy/league/lg1/home",
        ParsedLeagueId = "lg1",
        UrlHasTeamId = false,
    };

    [Fact]
    public void NothingAboutTeamsShowsUntilThereIsAUrl()
    {
        var state = SettingsFormState.Build(new SettingsInput(), Now);

        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void AUrlCarryingItsOwnTeamIdHidesTheEntirePickerBlock()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with
            {
                UrlHasTeamId = true,
                AvailableTeams = TwoTeams,
                IsLoadingTeams = true,
                TeamsError = "Couldn't load teams: boom",
            },
            Now);

        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsLoadTeamsButton);

        // SettingsView.swift:41 nests the error inside the same `if` - it is not a
        // free-floating error row.
        Assert.False(state.ShowsTeamsError);
        Assert.Null(state.TeamsErrorText);
    }

    [Fact]
    public void LoadingReplacesThePicker()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { IsLoadingTeams = true, AvailableTeams = TwoTeams }, Now);

        Assert.True(state.ShowsLoadingTeams);
        Assert.False(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void TeamsArriveAndThePickerAppears()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { AvailableTeams = TwoTeams }, Now);

        Assert.True(state.ShowsTeamPicker);
        Assert.False(state.ShowsLoadingTeams);
        Assert.False(state.ShowsLoadTeamsButton);
    }

    [Fact]
    public void NoTeamsYetOffersLoadTeams()
    {
        var state = SettingsFormState.Build(LeagueOnly(), Now);

        Assert.True(state.ShowsLoadTeamsButton);
        Assert.False(state.ShowsTeamPicker);
    }

    [Fact]
    public void AnUnparseableUrlOffersNothingToClick()
    {
        var state = SettingsFormState.Build(
            new SettingsInput { RosterUrl = "not a url", ParsedLeagueId = null }, Now);

        Assert.False(state.ShowsLoadTeamsButton);
        Assert.False(state.ShowsTeamPicker);
    }

    [Fact]
    public void ATeamsErrorSurfacesUnderThePickerBlock()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { TeamsError = "Couldn't load teams: timed out" }, Now);

        Assert.True(state.ShowsTeamsError);
        Assert.Equal("Couldn't load teams: timed out", state.TeamsErrorText);
    }

    [Fact]
    public void ThePlaceholderIsAlwaysTheFirstOption()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { AvailableTeams = TwoTeams }, Now);

        Assert.Equal(
            new[]
            {
                new TeamOption("", "Select a team..."),
                new TeamOption("t1", "Bronx Bombers"),
                new TeamOption("t2", "Queens Crew"),
            },
            state.TeamOptions);
    }

    [Fact]
    public void TheStoredTeamIsTheSelectedOption()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { AvailableTeams = TwoTeams, SelectedTeamId = "t2" }, Now);

        Assert.Equal("t2", state.SelectedTeamOptionId);
    }

    [Fact]
    public void ATeamThatIsNoLongerInTheLeagueFallsBackToThePlaceholder()
    {
        // FetchTeamsAsync clears a stale selection, but the window can render between the team
        // list arriving and that write landing. Selecting an id that is not in the list leaves
        // a WPF ComboBox blank with no indication why.
        var state = SettingsFormState.Build(
            LeagueOnly() with { AvailableTeams = TwoTeams, SelectedTeamId = "gone" }, Now);

        Assert.Equal("", state.SelectedTeamOptionId);
    }

    [Fact]
    public void SyncingShowsTheSpinnerAndItsLabel()
    {
        var state = SettingsFormState.Build(LeagueOnly() with { IsSyncing = true }, Now);

        Assert.True(state.ShowsSyncSpinner);
        Assert.Equal("Syncing...", state.SyncStatusText);
    }

    [Fact]
    public void SyncingOutranksTheLastSyncedStamp()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { IsSyncing = true, LastSyncDate = Now.AddMinutes(-4) }, Now);

        Assert.Equal("Syncing...", state.SyncStatusText);
    }

    [Fact]
    public void TheLastSyncedStampReadsAsRelativeAge()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { LastSyncDate = Now.AddMinutes(-4) }, Now);

        Assert.False(state.ShowsSyncSpinner);
        Assert.Equal("Last synced: 4 minutes ago", state.SyncStatusText);
    }

    [Fact]
    public void ThereIsNoStatusLineBeforeTheFirstSync()
    {
        var state = SettingsFormState.Build(LeagueOnly(), Now);

        Assert.False(state.ShowsSyncStatus);
        Assert.Null(state.SyncStatusText);
    }

    [Fact]
    public void SyncNowNeedsATeamAndAnIdleSync()
    {
        Assert.False(SettingsFormState.Build(LeagueOnly(), Now).IsSyncNowEnabled);

        Assert.False(SettingsFormState.Build(
            LeagueOnly() with { HasEffectiveTeam = true, IsSyncing = true }, Now).IsSyncNowEnabled);

        Assert.True(SettingsFormState.Build(
            LeagueOnly() with { HasEffectiveTeam = true }, Now).IsSyncNowEnabled);
    }

    [Fact]
    public void ASyncErrorIsSurfacedVerbatim()
    {
        var state = SettingsFormState.Build(
            LeagueOnly() with { SyncError = "Fantrax API returned HTTP 403" }, Now);

        Assert.True(state.ShowsSyncError);
        Assert.Equal("Fantrax API returned HTTP 403", state.SyncErrorText);
    }

    [Fact]
    public void ThePlayerCountAppearsOnlyOnceThereArePlayers()
    {
        Assert.False(SettingsFormState.Build(LeagueOnly(), Now).ShowsPlayerCount);

        var loaded = SettingsFormState.Build(LeagueOnly() with { LoadedPlayerCount = 26 }, Now);

        Assert.True(loaded.ShowsPlayerCount);
        Assert.Equal("26 players loaded", loaded.PlayerCountText);
    }
}
