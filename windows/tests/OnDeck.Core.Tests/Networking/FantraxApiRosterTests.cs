using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class FantraxApiRosterTests
{
    private const string RosterJson = """
    {
      "responses": [{"data": {
        "displayedLists": {"periodList": ["12 (Sun Aug 6)", "13 (Mon Aug 7)", "14 (Fri Aug 8)"]},
        "tables": [{"rows": [
          {"statusId": 1, "scorer": {"scorerId": "s1", "name": "Shohei Ohtani-P",
            "teamShortName": "LAD", "posShortNames": "SP,DH"}},
          {"statusId": "2", "scorer": {"scorerId": "s2", "name": "Mookie Betts",
            "teamShortName": "LAD", "posShortNames": "OF, 2B"}},
          {"scorer": {"scorerId": "s3", "name": "No Status Guy"}},
          {"scorer": {"name": "No ScorerId Guy", "teamShortName": "BOS"}},
          {"notAScorer": {"name": "Ignored"}}
        ]}]
      }}]
    }
    """;

    private static (FantraxApi Api, StubHttpMessageHandler Handler) Create(
        DateTimeOffset now, params string[] jsonResponses)
    {
        var handler = new StubHttpMessageHandler();
        foreach (var json in jsonResponses) handler.EnqueueJson(json);

        var time = new FakeTimeProvider(now);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        return (new FantraxApi(handler.CreateClient(), time), handler);
    }

    private static string MethodOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("msgs")[0].GetProperty("method").GetString()!;

    private static JsonElement DataOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("msgs")[0].GetProperty("data").Clone();

    [Fact]
    public async Task FetchRosterAsync_RefetchesWithTodaysPeriod()
    {
        // 2026-08-08 at 14:00 local -> baseball date is Aug 8 -> period "14".
        var (api, handler) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        await api.FetchRosterAsync("lg1", "tm1");

        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal("getTeamRosterInfo", MethodOf(handler.RequestBodies[0]));

        var firstData = DataOf(handler.RequestBodies[0]);
        Assert.False(firstData.TryGetProperty("period", out _));

        var secondData = DataOf(handler.RequestBodies[1]);
        Assert.Equal("14", secondData.GetProperty("period").GetString());
        Assert.Equal("lg1", secondData.GetProperty("leagueId").GetString());
        Assert.Equal("tm1", secondData.GetProperty("teamId").GetString());
    }

    [Fact]
    public async Task FetchRosterAsync_UsesYesterdaysPeriodBeforeEightAm()
    {
        // 03:00 local on Aug 8 -> baseball date is Aug 7 -> period "13".
        var (api, handler) = Create(
            new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        await api.FetchRosterAsync("lg1", "tm1");

        Assert.Equal("13", DataOf(handler.RequestBodies[1]).GetProperty("period").GetString());
    }

    [Fact]
    public async Task FetchRosterAsync_SkipsTheSecondCallWhenNoPeriodMatches()
    {
        // Sept 8 is not in the periodList.
        var (api, handler) = Create(
            new DateTimeOffset(2026, 9, 8, 14, 0, 0, TimeSpan.Zero), RosterJson);

        var players = await api.FetchRosterAsync("lg1", "tm1");

        Assert.Single(handler.RequestBodies);
        Assert.Equal(3, players.Count);
    }

    [Fact]
    public async Task FetchRosterAsync_ParsesScorerRows()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var players = await api.FetchRosterAsync("lg1", "tm1");

        // The row without a scorerId and the row without a scorer are both dropped.
        Assert.Equal(3, players.Count);

        Assert.Equal("Shohei Ohtani-P", players[0].Name);
        Assert.Equal("LAD", players[0].TeamShortName);
        Assert.Equal(["SP", "DH"], players[0].Positions);
        Assert.Equal(1, players[0].StatusId);
    }

    [Fact]
    public async Task FetchRosterAsync_TrimsPositionsAndReadsNumericStringStatus()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var betts = (await api.FetchRosterAsync("lg1", "tm1"))[1];

        Assert.Equal(["OF", "2B"], betts.Positions);
        Assert.Equal(2, betts.StatusId);
    }

    [Fact]
    public async Task FetchRosterAsync_DefaultsMissingFields()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero), RosterJson, RosterJson);

        var noStatus = (await api.FetchRosterAsync("lg1", "tm1"))[2];

        Assert.Equal("No Status Guy", noStatus.Name);
        Assert.Equal("", noStatus.TeamShortName);
        Assert.Empty(noStatus.Positions);
        Assert.Equal(1, noStatus.StatusId);       // defaults to active
    }

    [Fact]
    public async Task FetchRosterAsync_ThrowsWhenNoPlayersParsed()
    {
        var (api, _) = Create(
            new DateTimeOffset(2026, 8, 8, 14, 0, 0, TimeSpan.Zero),
            """{"responses": [{"data": {"tables": []}}]}""");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchRosterAsync("lg1", "tm1"));
        Assert.Equal(FantraxErrorKind.InvalidResponse, error.Kind);
    }
}
