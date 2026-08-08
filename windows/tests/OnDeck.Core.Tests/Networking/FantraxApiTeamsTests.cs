using System.Net;
using System.Text.Json;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class FantraxApiTeamsTests
{
    private const string StandingsJson = """
    {
      "responses": [{"data": {"tableList": [{"rows": [
        {"teamId": "t2", "content": "Zulu Squad"},
        {"teamId": "t1", "content": "Alpha Squad"},
        {"teamId": "t2", "content": "Zulu Squad"},
        {"teamId": "", "content": "Nameless"},
        {"teamId": "t3", "content": ""},
        {"nested": {"teamId": "t4", "content": "Mike Team"}}
      ]}]}}]
    }
    """;

    private static (FantraxApi Api, StubHttpMessageHandler Handler) Create(params string[] jsonResponses)
    {
        var handler = new StubHttpMessageHandler();
        foreach (var json in jsonResponses) handler.EnqueueJson(json);
        return (new FantraxApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchTeamsAsync_PostsGetStandingsToTheLeagueEndpoint()
    {
        var (api, handler) = Create(StandingsJson);

        await api.FetchTeamsAsync("lg123");

        var request = handler.Requests[^1];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://www.fantrax.com/fxpa/req?leagueId=lg123", request.RequestUri!.AbsoluteUri);
        Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);

        using var body = JsonDocument.Parse(handler.RequestBodies[^1]);
        var message = body.RootElement.GetProperty("msgs")[0];
        Assert.Equal("getStandings", message.GetProperty("method").GetString());
        Assert.Equal("lg123", message.GetProperty("data").GetProperty("leagueId").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("uiv").GetInt32());
    }

    [Fact]
    public async Task FetchTeamsAsync_CollectsDedupesAndSortsByName()
    {
        var (api, _) = Create(StandingsJson);

        var teams = await api.FetchTeamsAsync("lg123");

        Assert.Equal(["Alpha Squad", "Mike Team", "Zulu Squad"], teams.Select(t => t.Name));
        Assert.Equal(["t1", "t4", "t2"], teams.Select(t => t.Id));
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsWhenNoTeamsFound()
    {
        var (api, _) = Create("""{"responses": [{"data": {}}]}""");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.NoTeamsFound, error.Kind);
        Assert.Equal("No teams found in league", error.Message);
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsOnHttpError()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var api = new FantraxApi(handler.CreateClient());

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.HttpError, error.Kind);
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("Fantrax API returned HTTP 503", error.Message);
    }

    [Fact]
    public async Task FetchTeamsAsync_ThrowsWhenBodyIsNotAJsonObject()
    {
        var (api, _) = Create("[1, 2, 3]");

        var error = await Assert.ThrowsAsync<FantraxException>(() => api.FetchTeamsAsync("lg123"));
        Assert.Equal(FantraxErrorKind.InvalidResponse, error.Kind);
        Assert.Equal("Invalid response from Fantrax", error.Message);
    }
}
