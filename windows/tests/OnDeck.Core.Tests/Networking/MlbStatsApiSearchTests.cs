using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiSearchTests
{
    private const string TwoOhtanisJson = """
    {
      "people": [
        {"id": 1, "fullName": "Shohei Ohtani", "currentTeam": {"id": 108, "name": "Los Angeles Angels"}},
        {"id": 2, "fullName": "Shohei Ohtani", "currentTeam": {"id": 119, "name": "Los Angeles Dodgers"}}
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
    public async Task SearchPlayerAsync_UrlEncodesTheNameAndHydratesTeam()
    {
        var (api, handler) = Create(TwoOhtanisJson);

        await api.SearchPlayerAsync("Shohei Ohtani", null);

        // AbsoluteUri keeps percent-encoding; ToString() unescapes it for display.
        var url = handler.LastUri!.AbsoluteUri;
        Assert.Contains("/v1/people/search?names=Shohei%20Ohtani", url);
        Assert.Contains("hydrate=currentTeam", url);
    }

    [Fact]
    public async Task SearchPlayerAsync_DisambiguatesByFantraxAbbreviation()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(2, await api.SearchPlayerAsync("Shohei Ohtani", "LAD"));
    }

    [Fact]
    public async Task SearchPlayerAsync_DisambiguatesByPartialTeamName()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", "Angels"));
    }

    [Fact]
    public async Task SearchPlayerAsync_FallsBackToFirstResultWhenTeamDoesNotMatch()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", "BOS"));
    }

    [Fact]
    public async Task SearchPlayerAsync_FallsBackToFirstResultWithoutATeam()
    {
        var (api, _) = Create(TwoOhtanisJson);
        Assert.Equal(1, await api.SearchPlayerAsync("Shohei Ohtani", null));
    }

    [Fact]
    public async Task SearchPlayerAsync_IgnoresPeopleWithoutACurrentTeam()
    {
        const string json = """
        {
          "people": [
            {"id": 10, "fullName": "Prospect Guy"},
            {"id": 11, "fullName": "Prospect Guy", "currentTeam": {"id": 119, "name": "Los Angeles Dodgers"}}
          ]
        }
        """;
        var (api, _) = Create(json);

        Assert.Equal(11, await api.SearchPlayerAsync("Prospect Guy", "LAD"));
    }

    [Theory]
    [InlineData("""{"people": []}""")]
    [InlineData("""{}""")]
    public async Task SearchPlayerAsync_ReturnsNullWhenNobodyFound(string json)
    {
        var (api, _) = Create(json);
        Assert.Null(await api.SearchPlayerAsync("Nobody", null));
    }
}
