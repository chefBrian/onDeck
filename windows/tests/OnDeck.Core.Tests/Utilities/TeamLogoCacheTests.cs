using System.Net;
using OnDeck.Core.Tests.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.Core.Tests.Utilities;

public class TeamLogoCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-logo-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task GetAsync_DownloadsAndCachesTheLogo()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        var path = await cache.GetAsync(119, 32);

        Assert.NotNull(path);
        Assert.Equal(PngBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GetAsync_RequestsTheMidfieldSpotsUrl()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        await cache.GetAsync(119, 32);

        Assert.Equal(
            "https://midfield.mlbstatic.com/v1/team/119/spots/32", handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetAsync_SkipsTheNetworkWhenAlreadyCached()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        await cache.GetAsync(119, 32);
        await cache.GetAsync(119, 32);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAsync_KeepsSizesApart()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        var small = await cache.GetAsync(119, 16);
        var large = await cache.GetAsync(119, 32);

        Assert.NotEqual(small, large);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullOnAFailedRequest()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        Assert.Null(await cache.GetAsync(119, 32));
    }

    [Fact]
    public async Task GetAsync_RejectsABodyThatIsNotAPng()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueJson("""{"error":"nope"}""");
        var cache = new TeamLogoCache(handler.CreateClient(), _directory);

        Assert.Null(await cache.GetAsync(119, 32));
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory).Length > 0);
    }

    [Fact]
    public void FilePath_IsNullUntilCached()
    {
        var cache = new TeamLogoCache(new StubHttpMessageHandler().CreateClient(), _directory);

        Assert.Null(cache.FilePath(119, 32));
    }

    [Fact]
    public void DefaultCacheDirectory_SitsBesideTheHeadshotCache()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "onDeck",
                "TeamLogos"),
            TeamLogoCache.DefaultCacheDirectory());
    }
}
