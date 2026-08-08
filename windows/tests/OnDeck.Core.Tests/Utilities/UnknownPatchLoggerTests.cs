using System.Text.Json;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class UnknownPatchLoggerTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Record_CapturesOpPathAndFrom()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("replace", "/a/b", "/c", Json("42"));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("replace", entry.Op);
        Assert.Equal("/a/b", entry.Path);
        Assert.Equal("/c", entry.From);
        Assert.Equal("42", entry.ValuePreview);
    }

    [Fact]
    public void Record_SamplesEachKeyAtMostMaxPerKeyTimes()
    {
        var logger = new UnknownPatchLogger();
        for (var i = 0; i < 10; i++) logger.Record("add", "/same", null, null);

        Assert.Equal(UnknownPatchLogger.MaxPerKey, logger.Entries.Count);
        Assert.Equal(10, logger.Counts["add|/same"]);
    }

    [Fact]
    public void Record_TracksDistinctKeysIndependently()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/one", null, null);
        logger.Record("remove", "/one", null, null);   // same path, different op
        logger.Record("add", "/two", null, null);

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal(1, logger.Counts["add|/one"]);
        Assert.Equal(1, logger.Counts["remove|/one"]);
        Assert.Equal(1, logger.Counts["add|/two"]);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("null", "null")]
    [InlineData("\"text\"", "\"text\"")]
    [InlineData("{\"id\":5}", "{\"id\":5}")]
    [InlineData("[1,2,3]", "[1,2,3]")]
    public void Record_RendersValuePreview(string? json, string expected)
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/p", null, json is null ? null : Json(json));

        Assert.Equal(expected, Assert.Single(logger.Entries).ValuePreview);
    }

    [Fact]
    public void Record_TruncatesLongPreviewsTo120Characters()
    {
        var logger = new UnknownPatchLogger();
        logger.Record("add", "/p", null, Json($"\"{new string('x', 500)}\""));

        Assert.Equal(120, Assert.Single(logger.Entries).ValuePreview.Length);
    }
}
