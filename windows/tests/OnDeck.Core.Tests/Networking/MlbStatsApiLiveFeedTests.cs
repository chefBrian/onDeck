using OnDeck.Core.Networking;
using OnDeck.Core.Tests.Fixtures;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiLiveFeedTests
{
    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);
        return (new MlbStatsApi(handler.CreateClient()), handler);
    }

    [Fact]
    public async Task FetchLiveFeedAsync_RequestsTheV11FeedForTheGame()
    {
        var (api, handler) = Create(LiveFeedPatcherFixtures.BaseFeedJson);

        await api.FetchLiveFeedAsync(776543);

        Assert.Equal(
            "https://statsapi.mlb.com/api/v1.1/game/776543/feed/live",
            handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FetchLiveFeedAsync_DecodesThroughLiveFeedDecoder()
    {
        var (api, _) = Create(LiveFeedPatcherFixtures.BaseFeedJson);

        var feed = await api.FetchLiveFeedAsync(776543);

        Assert.Equal("20260416_180000", feed.TimeStamp);
        Assert.Equal("Live", feed.GameState);
        Assert.Equal(1, feed.CurrentBatterId);
        Assert.Equal([2], feed.HomePitchers);
    }

    [Fact]
    public async Task FetchGameChangesAsync_RequestsChangesSinceTheTimestamp()
    {
        var (api, handler) = Create("""{"dates": []}""");

        await api.FetchGameChangesAsync(new DateTimeOffset(2026, 8, 8, 14, 30, 0, TimeSpan.Zero));

        var url = handler.LastUri!.AbsoluteUri;
        Assert.StartsWith("https://statsapi.mlb.com/api/v1/game/changes?", url);
        Assert.Contains("sportId=1", url);
        Assert.Contains("updatedSince=2026-08-08T14%3A30%3A00Z", url);
    }

    [Fact]
    public async Task FetchGameChangesAsync_FlattensGamePksAcrossDates()
    {
        const string json = """
        {
          "dates": [
            {"games": [{"gamePk": 1}, {"gamePk": 2}]},
            {"games": [{"gamePk": 3}, {"gamePk": 1}]}
          ]
        }
        """;
        var (api, _) = Create(json);

        var changed = await api.FetchGameChangesAsync(DateTimeOffset.UnixEpoch);

        Assert.Equal([1, 2, 3], changed.Order());
    }

    [Fact]
    public async Task FetchGameChangesAsync_ReturnsEmptySetWhenNoDates()
    {
        var (api, _) = Create("""{}""");
        Assert.Empty(await api.FetchGameChangesAsync(DateTimeOffset.UnixEpoch));
    }
}
