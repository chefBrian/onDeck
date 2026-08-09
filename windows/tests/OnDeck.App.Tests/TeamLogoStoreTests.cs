using System.IO;
using OnDeck.App.Views;
using OnDeck.Core.Utilities;

namespace OnDeck.App.Tests;

public class TeamLogoStoreTests : IDisposable
{
    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ondeck-logo-store-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private TeamLogoStore Store(StubHttpMessageHandler handler) =>
        new(new TeamLogoCache(handler.CreateClient(), _directory), size: 32);

    [Fact]
    public void PathFor_IsNullBeforeAnythingIsFetched()
    {
        Assert.Null(Store(new StubHttpMessageHandler()).PathFor(119));
    }

    [Fact]
    public async Task PathFor_ReturnsTheFileOnceFetched()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(Path.Combine(_directory, "119_32.png"), store.PathFor(119));
    }

    [Fact]
    public async Task Prefetch_RaisesChangedWhenALogoLands()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);
        var changes = 0;
        store.Changed += () => changes++;

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Prefetch_DoesNotRefetchACachedLogo()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119]);
        await store.DrainAsync();
        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Prefetch_CollapsesRepeatRequestsForTheSameTeam()
    {
        // A rebuild every 10s with the logo still missing must not queue a request per rebuild.
        var handler = new StubHttpMessageHandler();
        handler.EnqueueBytes(PngBytes);
        var store = Store(handler);

        store.Prefetch([119, 119]);
        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Prefetch_StaysQuietWhenTheFetchFails()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueStatus(System.Net.HttpStatusCode.NotFound);
        var store = Store(handler);
        var changes = 0;
        store.Changed += () => changes++;

        store.Prefetch([119]);
        await store.DrainAsync();

        Assert.Equal(0, changes);
        Assert.Null(store.PathFor(119));
    }
}
