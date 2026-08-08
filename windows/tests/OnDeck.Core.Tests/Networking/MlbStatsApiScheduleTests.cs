using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiScheduleTests
{
    private const string ScheduleJson = """
    {
      "dates": [
        {
          "games": [
            {
              "gamePk": 776543,
              "gameDate": "2026-08-08T23:10:00Z",
              "status": {"abstractGameState": "Preview", "detailedState": "Scheduled"},
              "teams": {
                "away": {
                  "team": {"id": 137, "name": "San Francisco Giants"},
                  "probablePitcher": {"id": 592866}
                },
                "home": {
                  "team": {"id": 119, "name": "Los Angeles Dodgers"},
                  "probablePitcher": {"id": 605483}
                }
              },
              "broadcasts": [
                {"type": "TV", "callSign": "SNLA", "availability": {"availabilityCode": "regional"}},
                {"type": "TV", "callSign": "Peacock", "availability": {"availabilityCode": "exclusive"}},
                {"type": "TV", "availability": {"availabilityCode": "exclusive"}}
              ],
              "lineups": {
                "homePlayers": [{"id": 660271}, {"id": 605141}],
                "awayPlayers": [{"id": 592885}]
              }
            }
          ]
        }
      ]
    }
    """;

    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);
        return (new MlbStatsApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchScheduleAsync_RequestsHydratedScheduleForTheDate()
    {
        var (api, handler) = Create(ScheduleJson);

        await api.FetchScheduleAsync(new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero));

        var url = handler.LastUri!.ToString();
        Assert.StartsWith("https://statsapi.mlb.com/api/v1/schedule?", url);
        Assert.Contains("sportId=1", url);
        Assert.Contains("date=2026-08-08", url);
        Assert.Contains("hydrate=team,broadcasts,probablePitcher,lineups", url);
    }

    [Fact]
    public async Task FetchScheduleAsync_MapsGameFields()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Equal(776543, game.Id);
        Assert.Equal("Los Angeles Dodgers", game.HomeTeam);
        Assert.Equal("San Francisco Giants", game.AwayTeam);
        Assert.Equal(119, game.HomeTeamId);
        Assert.Equal(137, game.AwayTeamId);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 23, 10, 0, TimeSpan.Zero), game.StartTime);
        Assert.Equal(605483, game.HomeProbablePitcherId);
        Assert.Equal(592866, game.AwayProbablePitcherId);
    }

    [Fact]
    public async Task FetchScheduleAsync_KeepsNamedBroadcastsAndFlagsExclusive()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        // The third broadcast has no callSign and is dropped.
        Assert.Equal(2, game.Broadcasts.Count);
        Assert.Equal("SNLA", game.Broadcasts[0].CallSign);
        Assert.False(game.Broadcasts[0].IsExclusive);
        Assert.Equal("Peacock", game.Broadcasts[1].CallSign);
        Assert.True(game.Broadcasts[1].IsExclusive);
    }

    [Fact]
    public async Task FetchScheduleAsync_ReadsSubmittedLineups()
    {
        var (api, _) = Create(ScheduleJson);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Equal([660271, 605141], game.HomeLineup);
        Assert.Equal([592885], game.AwayLineup);
    }

    [Fact]
    public async Task FetchScheduleAsync_DefaultsMissingOptionalSections()
    {
        const string json = """
        {
          "dates": [{"games": [{
            "gamePk": 1,
            "gameDate": "2026-08-08T23:10:00Z",
            "status": {"abstractGameState": "Preview"},
            "teams": {
              "away": {"team": {"id": 1, "name": "A"}},
              "home": {"team": {"id": 2, "name": "H"}}
            }
          }]}]
        }
        """;
        var (api, _) = Create(json);

        var game = Assert.Single(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));

        Assert.Null(game.HomeProbablePitcherId);
        Assert.Null(game.AwayProbablePitcherId);
        Assert.Empty(game.Broadcasts);
        Assert.Empty(game.HomeLineup);
        Assert.Empty(game.AwayLineup);
    }

    [Fact]
    public async Task FetchScheduleAsync_FlattensMultipleDates()
    {
        const string json = """
        {
          "dates": [
            {"games": [{"gamePk": 1, "gameDate": "2026-08-08T23:10:00Z",
              "status": {"abstractGameState": "Preview"},
              "teams": {"away": {"team": {"id": 1, "name": "A"}}, "home": {"team": {"id": 2, "name": "H"}}}}]},
            {"games": [{"gamePk": 2, "gameDate": "2026-08-09T23:10:00Z",
              "status": {"abstractGameState": "Preview"},
              "teams": {"away": {"team": {"id": 3, "name": "C"}}, "home": {"team": {"id": 4, "name": "D"}}}}]}
          ]
        }
        """;
        var (api, _) = Create(json);

        var games = await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch);

        Assert.Equal([1, 2], games.Select(g => g.Id));
    }

    [Fact]
    public async Task FetchScheduleAsync_ReturnsEmptyWhenNoDates()
    {
        var (api, _) = Create("""{"dates": []}""");
        Assert.Empty(await api.FetchScheduleAsync(DateTimeOffset.UnixEpoch));
    }
}
