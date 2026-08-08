using System.Text.Json;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class PatchOperationTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void TryParse_ReadsOpPathValueAndFrom()
    {
        var op = PatchOperation.TryParse(Json("""
            {"op": "copy", "path": "/a", "from": "/b", "value": 7}
            """));

        Assert.NotNull(op);
        Assert.Equal("copy", op.Op);
        Assert.Equal("/a", op.Path);
        Assert.Equal("/b", op.From);
        Assert.Equal(7, op.Value!.Value.GetInt32());
    }

    [Fact]
    public void TryParse_LeavesValueAndFromNullWhenAbsent()
    {
        var op = PatchOperation.TryParse(Json("""{"op": "remove", "path": "/a"}"""));

        Assert.NotNull(op);
        Assert.Null(op.Value);
        Assert.Null(op.From);
    }

    [Theory]
    [InlineData("""{"path": "/a"}""")]              // no op
    [InlineData("""{"op": "add"}""")]               // no path
    [InlineData("""{"op": 1, "path": "/a"}""")]     // op not a string
    [InlineData("""{"op": "add", "path": 2}""")]    // path not a string
    [InlineData("""[1, 2]""")]                      // not an object
    public void TryParse_ReturnsNullForMalformedOps(string json)
    {
        Assert.Null(PatchOperation.TryParse(Json(json)));
    }

    [Fact]
    public void ParseArray_SkipsMalformedEntries()
    {
        var ops = PatchOperation.ParseArray(Json("""
            [
              {"op": "replace", "path": "/a", "value": 1},
              {"op": "replace"},
              {"op": "remove", "path": "/b"}
            ]
            """));

        Assert.Equal(["/a", "/b"], ops.Select(o => o.Path));
    }

    [Fact]
    public void ParseArray_ReturnsEmptyForNonArray()
    {
        Assert.Empty(PatchOperation.ParseArray(Json("""{"op": "add", "path": "/a"}""")));
    }
}
