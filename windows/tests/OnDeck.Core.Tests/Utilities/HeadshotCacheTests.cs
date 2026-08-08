using System.Net;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class HeadshotCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-headshot-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task PrefetchAsync_WritesDownloadedHeadshots()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        var path = cache.FilePath(660271);
        Assert.NotNull(path);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PrefetchAsync_RequestsTheMlbStaticHeadshotUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        Assert.Equal(
            "https://img.mlbstatic.com/mlb-photos/image/upload/"
                + "d_people:generic:headshot:67:current.png/w_128/q_auto:best/v1/people/660271/headshot/67/current",
            handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PrefetchAsync_SkipsIdsAlreadyOnDisk()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "660271.png"), PngBytes);

        var handler = new StubHttpMessageHandler();
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([660271]);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PrefetchAsync_DoesNotWriteNonPngPayloads()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes([0x3C, 0x68, 0x74, 0x6D, 0x6C]);   // "<html"
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([1]);

        Assert.Null(cache.FilePath(1));
    }

    [Fact]
    public async Task PrefetchAsync_SwallowsHttpErrors()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        var cache = new HeadshotCache(handler.CreateClient(), _directory);

        await cache.PrefetchAsync([1]);

        Assert.Null(cache.FilePath(1));
    }

    [Fact]
    public void FilePath_ReturnsNullWhenNotCached()
    {
        var cache = new HeadshotCache(new StubHttpMessageHandler().CreateClient(), _directory);
        Assert.Null(cache.FilePath(999));
    }

    [Fact]
    public void DefaultCacheDirectory_LivesUnderLocalAppData()
    {
        var path = HeadshotCache.DefaultCacheDirectory();

        Assert.Contains("onDeck", path);
        Assert.EndsWith("Headshots", path);
    }
}
