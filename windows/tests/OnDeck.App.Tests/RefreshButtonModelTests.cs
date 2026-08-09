using Microsoft.Extensions.Time.Testing;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class RefreshButtonModelTests
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.2);

    [Fact]
    public void StartsIdle()
    {
        Assert.Equal(RefreshButtonState.Idle, new RefreshButtonModel().State);
    }

    [Fact]
    public async Task ShowsDoneThenReturnsToIdleOnSuccess()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => Task.FromResult(true));

        Assert.Equal(RefreshButtonState.Done, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task ShowsFailedThenReturnsToIdleOnFailure()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => Task.FromResult(false));

        Assert.Equal(RefreshButtonState.Failed, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task SpinsWhileTheSyncIsInFlight()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var sync = new TaskCompletionSource<bool>();

        var click = model.ClickAsync(() => sync.Task);

        Assert.Equal(RefreshButtonState.Spinning, model.State);
        sync.SetResult(true);
        await Task.Yield();
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }

    [Fact]
    public async Task IgnoresAClickWhileAlreadyRunning()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var sync = new TaskCompletionSource<bool>();
        var calls = 0;

        var first = model.ClickAsync(() => { calls++; return sync.Task; });
        await model.ClickAsync(() => { calls++; return Task.FromResult(true); });

        Assert.Equal(1, calls);

        sync.SetResult(true);
        await Task.Yield();
        time.Advance(Hold);
        await first;
    }

    [Fact]
    public async Task RaisesChangedOnEveryTransition()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);
        var seen = new List<RefreshButtonState>();
        model.Changed += () => seen.Add(model.State);

        var click = model.ClickAsync(() => Task.FromResult(true));
        time.Advance(Hold);
        await click;

        Assert.Equal(
            new[] { RefreshButtonState.Spinning, RefreshButtonState.Done, RefreshButtonState.Idle },
            seen);
    }

    [Fact]
    public async Task ReturnsToIdleWhenTheSyncThrows()
    {
        var time = new FakeTimeProvider();
        var model = new RefreshButtonModel(time);

        var click = model.ClickAsync(() => throw new InvalidOperationException("network"));

        Assert.Equal(RefreshButtonState.Failed, model.State);
        time.Advance(Hold);
        await click;
        Assert.Equal(RefreshButtonState.Idle, model.State);
    }
}
