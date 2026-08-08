using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Managers;
using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Networking;

namespace OnDeck.Core.Tests.Managers;

public class ScheduleManagerTests
{
    private const string TwoGamesJson = """
    {
      "dates": [{"games": [
        {"gamePk": 1, "gameDate": "2026-08-08T23:10:00Z", "status": {"abstractGameState": "Preview"},
         "teams": {"away": {"team": {"id": 137, "name": "San Francisco Giants"}},
                   "home": {"team": {"id": 119, "name": "Los Angeles Dodgers"}}}},
        {"gamePk": 2, "gameDate": "2026-08-08T23:10:00Z", "status": {"abstractGameState": "Preview"},
         "teams": {"away": {"team": {"id": 111, "name": "Boston Red Sox"}},
                   "home": {"team": {"id": 147, "name": "New York Yankees"}}}}
      ]}]
    }
    """;

    private static (ScheduleManager Manager, StubHttpMessageHandler Handler) Create(
        string json, DateTimeOffset? now = null)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);

        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);

        return (new ScheduleManager(new MlbStatsApi(handler.CreateClient(), time), time), handler);
    }

    [Fact]
    public async Task FetchScheduleAsync_KeepsOnlyGamesInvolvingTheGivenTeams()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Equal([1], manager.TodaysGames.Select(g => g.Id));
        Assert.Null(manager.Error);
    }

    [Fact]
    public async Task FetchScheduleAsync_MatchesTheAwaySideToo()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Boston Red Sox" });

        Assert.Equal([2], manager.TodaysGames.Select(g => g.Id));
    }

    [Fact]
    public async Task FetchScheduleAsync_ReturnsNothingWhenNoTeamsMatch()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Seattle Mariners" });

        Assert.Empty(manager.TodaysGames);
    }

    [Fact]
    public async Task FetchScheduleAsync_UsesTheBaseballDateBeforeEightAm()
    {
        var (manager, handler) = Create(
            TwoGamesJson, new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero));

        await manager.FetchScheduleAsync(new HashSet<string>());

        Assert.Contains("date=2026-08-07", handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FetchScheduleAsync_RecordsErrorsWithoutThrowing()
    {
        var failingHandler = new StubHttpMessageHandler();
        failingHandler.EnqueueStatus(System.Net.HttpStatusCode.InternalServerError);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero));
        var manager = new ScheduleManager(new MlbStatsApi(failingHandler.CreateClient(), time), time);

        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Empty(manager.TodaysGames);
        Assert.StartsWith("Schedule fetch failed:", manager.Error);
    }

    [Fact]
    public async Task FetchScheduleAsync_ClearsAPreviousError()
    {
        var (manager, _) = Create(TwoGamesJson);

        await manager.FetchScheduleAsync(new HashSet<string> { "Los Angeles Dodgers" });

        Assert.Null(manager.Error);
    }
}
