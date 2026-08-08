using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Models;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class RosterManagerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 14, 0, 0, TimeSpan.Zero);

    /// <summary>One Fantrax roster response, then one MLB search response per roster row.</summary>
    private static (RosterManager Manager, InMemorySettingsStore Settings) Create(
        string rosterJson, params string[] searchJson)
    {
        var fantraxHandler = new StubHttpMessageHandler();
        fantraxHandler.EnqueueJson(rosterJson);

        var mlbHandler = new StubHttpMessageHandler();
        foreach (var json in searchJson) mlbHandler.EnqueueJson(json);

        var time = new FakeTimeProvider(Now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        var settings = new InMemorySettingsStore();
        var manager = new RosterManager(
            new FantraxApi(fantraxHandler.CreateClient(), time),
            new MlbStatsApi(mlbHandler.CreateClient(), time),
            settings,
            headshots: null,
            timeProvider: time);

        return (manager, settings);
    }

    private static RosterManager Offline(InMemorySettingsStore settings) =>
        new(new FantraxApi(new StubHttpMessageHandler().CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            settings,
            headshots: null);

    // $$$ / {{{ }}} so the JSON's doubled closing braces stay literal.
    private static string Roster(params string[] rows) => $$$"""
        {"responses": [{"data": {"tables": [{"rows": [{{{string.Join(",", rows)}}}]}]}}]}
        """;

    private static string Row(string name, string team, string positions, int statusId = 1) => $$$"""
        {"statusId": {{{statusId}}}, "scorer": {"scorerId": "s", "name": "{{{name}}}",
          "teamShortName": "{{{team}}}", "posShortNames": "{{{positions}}}"}}
        """;

    private static string Person(int id) => $$$"""
        {"people": [{"id": {{{id}}}, "fullName": "x", "currentTeam": {"id": 1, "name": "Los Angeles Dodgers"}}]}
        """;

    [Fact]
    public async Task SyncRosterAsync_ResolvesMlbIdsAndCleansNames()
    {
        var (manager, _) = Create(Roster(Row("T.J. Rumfield-P", "LAD", "SP")), Person(500));

        await manager.SyncRosterAsync("lg", "tm");

        var player = Assert.Single(manager.Players);
        Assert.Equal(500, player.Id);
        Assert.Equal("TJ Rumfield", player.Name);
        Assert.Equal("Los Angeles Dodgers", player.Team);
        Assert.Contains(PlayerPosition.Pitcher, player.Positions);
        Assert.Null(manager.Error);
        Assert.Equal(Now, manager.LastSyncDate);
        Assert.False(manager.IsSyncing);
    }

    [Theory]
    [InlineData("SP", PlayerPosition.Pitcher)]
    [InlineData("RP", PlayerPosition.Pitcher)]
    [InlineData("P", PlayerPosition.Pitcher)]
    [InlineData("OF", PlayerPosition.Hitter)]
    [InlineData("C", PlayerPosition.Hitter)]
    public async Task SyncRosterAsync_ClassifiesPositions(string code, PlayerPosition expected)
    {
        var (manager, _) = Create(Roster(Row("Guy", "LAD", code)), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Equal([expected], Assert.Single(manager.Players).Positions);
    }

    [Fact]
    public async Task SyncRosterAsync_MergesTwoWayPlayersByMlbId()
    {
        // Ohtani appears twice - once as -P, once as -DH - and both resolve to the same MLB ID.
        var (manager, _) = Create(
            Roster(Row("Shohei Ohtani-P", "LAD", "SP", statusId: 2),
                   Row("Shohei Ohtani-DH", "LAD", "DH", statusId: 1)),
            Person(660271), Person(660271));

        await manager.SyncRosterAsync("lg", "tm");

        var ohtani = Assert.Single(manager.Players);
        Assert.True(ohtani.IsPitcher);
        Assert.True(ohtani.IsHitter);
        Assert.Equal(new HashSet<string> { "SP", "DH" }, ohtani.FantraxPositions.ToHashSet());
        Assert.Equal(RosterStatus.Active, ohtani.RosterStatus);   // lower statusId wins
    }

    [Fact]
    public async Task SyncRosterAsync_SortsPlayersByName()
    {
        var (manager, _) = Create(
            Roster(Row("Zeta Guy", "LAD", "OF"), Row("Alpha Guy", "LAD", "OF")),
            Person(2), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Equal(["Alpha Guy", "Zeta Guy"], manager.Players.Select(p => p.Name));
    }

    [Fact]
    public async Task SyncRosterAsync_MapsStatusIdToRosterStatus()
    {
        var (manager, _) = Create(Roster(Row("Guy", "LAD", "OF", statusId: 9)), Person(1));

        await manager.SyncRosterAsync("lg", "tm");

        var player = Assert.Single(manager.Players);
        Assert.Equal(RosterStatus.Minors, player.RosterStatus);
        Assert.True(player.IsUnavailable);
    }

    [Fact]
    public async Task SyncRosterAsync_RecordsErrorAndKeepsPreviousRosterOnFailure()
    {
        var fantraxHandler = new StubHttpMessageHandler();
        fantraxHandler.EnqueueJson("[]");   // not a JSON object -> InvalidResponse
        var manager = new RosterManager(
            new FantraxApi(fantraxHandler.CreateClient()),
            new MlbStatsApi(new StubHttpMessageHandler().CreateClient()),
            new InMemorySettingsStore(),
            headshots: null);

        await manager.SyncRosterAsync("lg", "tm");

        Assert.Empty(manager.Players);
        Assert.StartsWith("Roster sync failed:", manager.Error);
        Assert.False(manager.IsSyncing);
    }

    [Fact]
    public async Task SyncRosterAsync_WritesAndReloadsTheCache()
    {
        var (manager, settings) = Create(
            Roster(Row("Shohei Ohtani-P", "LAD", "SP")), Person(660271));

        await manager.SyncRosterAsync("lg", "tm");
        Assert.NotNull(settings.RosterCacheJson);

        var reloaded = Offline(settings);
        reloaded.LoadCachedRoster();

        Assert.Equal(manager.Players, reloaded.Players);
    }

    [Fact]
    public void LoadCachedRoster_IgnoresMalformedJson()
    {
        var manager = Offline(new InMemorySettingsStore { RosterCacheJson = "{not json" });

        manager.LoadCachedRoster();

        Assert.Empty(manager.Players);
    }

    [Fact]
    public void LoadCachedRoster_DefaultsOptionalCacheFields()
    {
        var manager = Offline(new InMemorySettingsStore
        {
            RosterCacheJson = """[{"id": 1, "name": "Guy", "team": "LAD", "positions": ["Hitter"]}]""",
        });

        manager.LoadCachedRoster();

        var player = Assert.Single(manager.Players);
        Assert.Empty(player.FantraxPositions);
        Assert.Equal(RosterStatus.Active, player.RosterStatus);
    }
}
