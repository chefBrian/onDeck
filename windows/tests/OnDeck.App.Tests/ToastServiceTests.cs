using System.IO;
using System.Net.Http;
using OnDeck.App.Notifications;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Tests;

public class ToastServiceTests : IDisposable
{
    private static readonly Uri Stream = new("https://www.mlb.com/tv/g776543");

    private readonly string _headshotDirectory = Path.Combine(
        Path.GetTempPath(), "ondeck-headshot-tests", Guid.NewGuid().ToString("N"));

    private readonly RecordingToastPresenter _presenter = new();
    private readonly RecordingSettingsStore _settings = new();

    public void Dispose()
    {
        if (Directory.Exists(_headshotDirectory))
        {
            Directory.Delete(_headshotDirectory, recursive: true);
        }
    }

    private ToastService Service() =>
        new(_settings, new HeadshotCache(new HttpClient(), _headshotDirectory), _presenter);

    private string WriteHeadshot(int playerId)
    {
        Directory.CreateDirectory(_headshotDirectory);
        var path = Path.Combine(_headshotDirectory, $"{playerId}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    [Fact]
    public async Task BattingIsShownWithItsPlan()
    {
        await Service().NotifyBattingAsync(
            "Mookie Betts", 605141, 776543, "SF 1 - LAD 2", "Bot 3", Stream);

        Assert.Single(_presenter.Shown);
        Assert.Equal("Mookie Betts is batting", _presenter.LastShown.Title);
        Assert.Equal("batting-776543-605141", _presenter.LastShown.Tag);
    }

    [Fact]
    public async Task EveryTypeReachesThePresenterWhenItsToggleIsOn()
    {
        var service = Service();

        await service.NotifyBattingAsync("A", 1, 9, "g", "i", null);
        await service.NotifyPitchingAsync("B", 2, 9, "g", "i", null);
        await service.NotifyAtBatResultAsync("C", 3, "d", null);
        await service.NotifyPitchingResultAsync("D", 4, "d", null);
        await service.NotifyNotInLineupAsync("E", 5, 9, "g", null);

        Assert.Equal(5, _presenter.Shown.Count);
    }

    [Fact]
    public async Task ADisabledToggleShowsNothingAtAll()
    {
        _settings.NotifyBatting = false;

        await Service().NotifyBattingAsync("A", 1, 9, "g", "i", null);

        Assert.Empty(_presenter.Shown);
    }

    [Fact]
    public async Task ACachedHeadshotIsPassedAlong()
    {
        var path = WriteHeadshot(605141);

        await Service().NotifyBattingAsync("Mookie Betts", 605141, 776543, "g", "i", null);

        Assert.Equal(path, _presenter.Shown[0].ImagePath);
    }

    [Fact]
    public async Task AMissingHeadshotIsNotAnError()
    {
        await Service().NotifyBattingAsync("Mookie Betts", 605141, 776543, "g", "i", null);

        Assert.Single(_presenter.Shown);
        Assert.Null(_presenter.Shown[0].ImagePath);
    }

    [Fact]
    public void PurgingBattingRemovesItsTag()
    {
        Service().PurgeBatting(776543, 605141);

        Assert.Equal(new[] { "batting-776543-605141" }, _presenter.Removed);
        Assert.Empty(_presenter.RemovedGroups);
    }

    [Fact]
    public void PurgingPitchingRemovesItsTag()
    {
        Service().PurgePitching(776543, 657277);

        Assert.Equal(new[] { "pitching-776543-657277" }, _presenter.Removed);
    }

    [Fact]
    public async Task PurgingNotInLineupRemovesTheWholeGroup()
    {
        // Game-scoped: players never in the lineup have no transition to hang a per-player
        // purge on, so this has to sweep the group rather than a tag.
        await Service().PurgeNotInLineupAsync(776543);

        Assert.Equal(new[] { "notInLineup-776543" }, _presenter.RemovedGroups);
        Assert.Empty(_presenter.Removed);
    }

    [Fact]
    public async Task PurgingEverythingClearsTheHistory()
    {
        await Service().PurgeAllAsync();

        Assert.Equal(1, _presenter.Cleared);
    }

    [Fact]
    public async Task PurgesAreNotGatedByTheToggles()
    {
        // A toast shown before the user turned its type off must still be removable.
        _settings.NotifyBatting = false;
        _settings.NotifyNotInLineup = false;
        var service = Service();

        service.PurgeBatting(776543, 605141);
        await service.PurgeNotInLineupAsync(776543);
        await service.PurgeAllAsync();

        Assert.Single(_presenter.Removed);
        Assert.Single(_presenter.RemovedGroups);
        Assert.Equal(1, _presenter.Cleared);
    }
}
