using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using OnDeck.Core.Networking;

namespace OnDeck.Core.Tests.Networking;

public class MlbStatsApiDiffPatchTests
{
    private static (MlbStatsApi Api, StubHttpMessageHandler Handler) Create(string json)
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson(json);

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 23, 45, 7, TimeSpan.Zero));
        return (new MlbStatsApi(handler.CreateClient(), time), handler);
    }

    [Fact]
    public async Task FetchDiffPatchAsync_FormsStartAndEndTimecodes()
    {
        var (api, handler) = Create("[]");

        await api.FetchDiffPatchAsync(776543, "20260808_234500");

        Assert.Equal(
            "https://statsapi.mlb.com/api/v1.1/game/776543/feed/live/diffPatch"
                + "?startTimecode=20260808_234500&endTimecode=20260808_234507",
            handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task FetchDiffPatchAsync_EmptyArrayIsNoChanges()
    {
        var (api, _) = Create("[]");
        Assert.IsType<DiffPatchResult.NoChanges>(await api.FetchDiffPatchAsync(1, "t"));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_CollectsOpsFromEveryDiffEntry()
    {
        const string json = """
        [
          {"diff": [
            {"op": "replace", "path": "/metaData/timeStamp", "value": "20260808_234507"},
            {"op": "replace", "path": "/liveData/linescore/outs", "value": 2}
          ]},
          {"diff": [{"op": "replace", "path": "/liveData/linescore/balls", "value": 1}]}
        ]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.Patches>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Equal(
            ["/metaData/timeStamp", "/liveData/linescore/outs", "/liveData/linescore/balls"],
            result.Operations.Select(o => o.Path));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_ObjectRootIsFullUpdateCarryingTheWholeBody()
    {
        // The dict-instead-of-array fallback: MLB returns a bare feed object during game
        // phase transitions. It resolves itself after a few cycles.
        const string json = """{"metaData": {"timeStamp": "20260808_234507"}, "gameData": {}}""";
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Contains("\"timeStamp\"", Encoding.UTF8.GetString(result.Json));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_EntryWithoutDiffIsFullUpdateCarryingThatEntry()
    {
        const string json = """
        [
          {"diff": [{"op": "replace", "path": "/a", "value": 1}]},
          {"metaData": {"timeStamp": "20260808_234507"}, "gameData": {"marker": "second-entry"}}
        ]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));

        var payload = Encoding.UTF8.GetString(result.Json);
        Assert.Contains("second-entry", payload);
        Assert.DoesNotContain("\"diff\"", payload);
    }

    [Fact]
    public async Task FetchDiffPatchAsync_ScalarRootIsFullUpdate()
    {
        var (api, _) = Create("\"unexpected\"");
        Assert.IsType<DiffPatchResult.FullUpdate>(await api.FetchDiffPatchAsync(1, "t"));
    }

    [Fact]
    public async Task FetchDiffPatchAsync_OpValuesOutliveTheParsedDocument()
    {
        // Regression: FetchDiffPatchAsync disposes its JsonDocument, so op values must be
        // cloned out of it. Reading Value after the call previously returned garbage.
        const string json = """[{"diff": [{"op": "replace", "path": "/a", "value": 42}]}]""";
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.Patches>(await api.FetchDiffPatchAsync(1, "t"));

        var op = Assert.Single(result.Operations);
        Assert.Equal(JsonValueKind.Number, op.Value!.Value.ValueKind);
        Assert.Equal(42, op.Value.Value.GetInt32());
    }

    [Fact]
    public async Task FetchDiffPatchAsync_SkipsMalformedOpsWithinADiff()
    {
        const string json = """
        [{"diff": [
          {"op": "replace", "path": "/a", "value": 1},
          {"op": "replace"},
          {"path": "/b"}
        ]}]
        """;
        var (api, _) = Create(json);

        var result = Assert.IsType<DiffPatchResult.Patches>(await api.FetchDiffPatchAsync(1, "t"));

        Assert.Equal(["/a"], result.Operations.Select(o => o.Path));
    }
}
