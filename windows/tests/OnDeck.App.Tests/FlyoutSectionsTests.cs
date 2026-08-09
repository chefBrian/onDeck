using OnDeck.App.Views;
using OnDeck.Core.Models;

namespace OnDeck.App.Tests;

public class FlyoutSectionsTests
{
    private static PlayerDisplay Row(int id) =>
        new()
        {
            Player = new Player(id, $"Player {id}", "Los Angeles Dodgers",
                new HashSet<PlayerPosition> { PlayerPosition.Hitter },
                new HashSet<string> { "OF" },
                RosterStatus.Active),
        };

    private static FlyoutInput Loaded(FlyoutInput input) =>
        input with { HasRosterUrl = true, LoadedPlayerCount = 12 };

    private static string? NoLogos(int teamId) => null;

    private static FlyoutSections Build(FlyoutInput input, bool isFloating = false) =>
        FlyoutSections.Build(input, isFloating, NoLogos);

    [Fact]
    public void ProjectsEachListIntoItsRowType()
    {
        var sections = Build(Loaded(new FlyoutInput
        {
            Active = [Row(1)],
            InGame = [Row(2), Row(3)],
            Upcoming = [Row(4)],
            Done = [Row(5)],
        }));

        Assert.Equal(new[] { 1 }, sections.Active.Select(row => row.PlayerId));
        Assert.Equal(new[] { 2, 3 }, sections.InGame.Select(row => row.PlayerId));
        Assert.Equal(new[] { 4 }, sections.Upcoming.Select(row => row.PlayerId));
        Assert.Equal(new[] { 5 }, sections.Done.Select(row => row.PlayerId));
        Assert.Null(sections.EmptyText);
    }

    [Fact]
    public void SectionsAreHiddenWhenEmpty()
    {
        var sections = Build(Loaded(new FlyoutInput { InGame = [Row(2)] }));

        Assert.False(sections.ShowsActive);
        Assert.True(sections.ShowsInGame);
        Assert.False(sections.ShowsUpcoming);
        Assert.False(sections.ShowsDone);
        Assert.False(sections.ShowsEmpty);
    }

    [Theory]
    [InlineData(true, true, 0, "Syncing roster...")]
    [InlineData(false, false, 0, "Set roster URL in Settings")]
    [InlineData(false, true, 0, "No players found")]
    [InlineData(false, true, 12, "No games today")]
    public void EmptyTextExplainsWhyThereIsNothing(
        bool isSyncing, bool hasRosterUrl, int loadedPlayerCount, string expected)
    {
        var sections = Build(new FlyoutInput
        {
            IsSyncing = isSyncing,
            HasRosterUrl = hasRosterUrl,
            LoadedPlayerCount = loadedPlayerCount,
        });

        Assert.True(sections.ShowsEmpty);
        Assert.Equal(expected, sections.EmptyText);
    }

    [Fact]
    public void EmptyTextIsAbsentWhenAnySectionHasRows()
    {
        var sections = Build(Loaded(new FlyoutInput { Done = [Row(5)] }));

        Assert.False(sections.ShowsEmpty);
        Assert.Null(sections.EmptyText);
    }

    [Fact]
    public void ErrorTextSurfacesTheSyncError()
    {
        var sections = Build(Loaded(new FlyoutInput { Error = "Couldn't reach Fantrax" }));

        Assert.True(sections.ShowsError);
        Assert.Equal("Couldn't reach Fantrax", sections.ErrorText);
    }

    [Fact]
    public void EverySectionIsFollowedByADividerInTheFlyout()
    {
        var sections = Build(Loaded(new FlyoutInput
        {
            Active = [Row(1)],
            InGame = [Row(2)],
            Upcoming = [Row(3)],
            Done = [Row(4)],
        }));

        Assert.True(sections.ActiveDivider);
        Assert.True(sections.InGameDivider);
        Assert.True(sections.UpcomingDivider);
        Assert.True(sections.DoneDivider);
    }

    [Fact]
    public void FloatingDropsTheTrailingDivider()
    {
        var sections = Build(
            Loaded(new FlyoutInput { Upcoming = [Row(3)], Done = [Row(4)] }), isFloating: true);

        Assert.True(sections.UpcomingDivider);      // Done still follows it
        Assert.False(sections.DoneDivider);         // nothing follows Done
    }

    [Fact]
    public void FloatingDropsUpcomingsDividerWhenItIsLast()
    {
        var sections = Build(Loaded(new FlyoutInput { Upcoming = [Row(3)] }), isFloating: true);

        Assert.False(sections.UpcomingDivider);
    }

    [Fact]
    public void FloatingDropsTheEmptyStatesDivider()
    {
        Assert.False(Build(Loaded(new FlyoutInput()), isFloating: true).EmptyDivider);
        Assert.True(Build(Loaded(new FlyoutInput())).EmptyDivider);
    }

    [Fact]
    public void HeaderControlsLandOnTheFirstVisibleSection()
    {
        Assert.Equal(
            FlyoutSectionKind.Active,
            Build(Loaded(new FlyoutInput { Active = [Row(1)], InGame = [Row(2)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.InGame,
            Build(Loaded(new FlyoutInput { InGame = [Row(2)], Done = [Row(4)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.Upcoming,
            Build(Loaded(new FlyoutInput { Upcoming = [Row(3)], Done = [Row(4)] })).HeaderControlsSection);

        Assert.Equal(
            FlyoutSectionKind.Done,
            Build(Loaded(new FlyoutInput { Done = [Row(4)] })).HeaderControlsSection);
    }

    [Fact]
    public void HeaderControlsFallBackToTheEmptyStateWithNothingToShow()
    {
        // Swift renders no header at all here, leaving the panel closable only from the Float
        // button. A borderless window with no taskbar entry needs its own close affordance.
        Assert.Equal(FlyoutSectionKind.Empty, Build(Loaded(new FlyoutInput())).HeaderControlsSection);
    }
}
