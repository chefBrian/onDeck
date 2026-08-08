namespace OnDeck.Core.Tests;

public class SingleThreadedContextTests
{
    [Fact]
    public void Run_RunsEveryContinuationOnOneThread()
    {
        var threads = new List<int>();

        SingleThreadedContext.Run(async () =>
        {
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Yield();
            threads.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(1);
            threads.Add(Environment.CurrentManagedThreadId);
        });

        Assert.Equal(3, threads.Count);
        Assert.Single(threads.Distinct());
    }

    [Fact]
    public void Run_PumpsPostedCallbacks()
    {
        var ran = false;

        SingleThreadedContext.Run(async () =>
        {
            SynchronizationContext.Current!.Post(_ => ran = true, null);
            Assert.False(ran);              // posted work is queued, not immediate
            await SingleThreadedContext.Settle();
            Assert.True(ran);
        });

        Assert.True(ran);
    }

    [Fact]
    public void Run_PropagatesExceptions()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            SingleThreadedContext.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }));

        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public void Run_RestoresThePreviousContext()
    {
        var before = SynchronizationContext.Current;

        SingleThreadedContext.Run(() => Task.CompletedTask);

        Assert.Same(before, SynchronizationContext.Current);
    }
}
