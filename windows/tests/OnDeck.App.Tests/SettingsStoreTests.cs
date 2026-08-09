using System.IO;

namespace OnDeck.App.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-settings-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void NotificationTogglesDefaultToOn()
    {
        var settings = new SettingsStore(_directory);

        Assert.True(settings.NotifyBatting);
        Assert.True(settings.NotifyPitching);
        Assert.True(settings.NotifyAtBatResult);
        Assert.True(settings.NotifyPitchingResult);
        Assert.True(settings.NotifyNotInLineup);
    }

    [Fact]
    public void EverythingElseDefaultsToEmpty()
    {
        var settings = new SettingsStore(_directory);

        Assert.Null(settings.RosterUrl);
        Assert.Null(settings.SelectedTeamId);
        Assert.False(settings.HideBenchPlayers);
        Assert.False(settings.AlwaysOpenPopout);
        Assert.Null(settings.RosterCacheJson);
    }

    [Fact]
    public void ValuesRoundTripThroughANewInstance()
    {
        var first = new SettingsStore(_directory)
        {
            RosterUrl = "https://www.fantrax.com/fantasy/league/lg1/team/roster;teamId=t1",
            SelectedTeamId = "t2",
            HideBenchPlayers = true,
            AlwaysOpenPopout = true,
            NotifyBatting = false,
            RosterCacheJson = """[{"id":101}]""",
        };

        var second = new SettingsStore(_directory);

        Assert.Equal(first.RosterUrl, second.RosterUrl);
        Assert.Equal("t2", second.SelectedTeamId);
        Assert.True(second.HideBenchPlayers);
        Assert.True(second.AlwaysOpenPopout);
        Assert.False(second.NotifyBatting);
        Assert.True(second.NotifyPitching);          // untouched flag keeps its default
        Assert.Equal("""[{"id":101}]""", second.RosterCacheJson);
    }

    [Fact]
    public void WritingCreatesTheDirectoryAndLeavesNoTempFile()
    {
        _ = new SettingsStore(_directory) { HideBenchPlayers = true };

        Assert.True(File.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void CorruptJsonFallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var settings = new SettingsStore(_directory);

        Assert.Null(settings.RosterUrl);
        Assert.True(settings.NotifyBatting);
    }

    [Fact]
    public void AnUnknownPropertyInTheFileIsIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"rosterUrl":"https://x","somethingNew":42}""");

        var settings = new SettingsStore(_directory);

        Assert.Equal("https://x", settings.RosterUrl);
    }

    [Fact]
    public void DefaultDirectoryLivesUnderAppData()
    {
        var directory = SettingsStore.DefaultDirectory();

        Assert.EndsWith("onDeck", directory);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), directory);
    }
}
