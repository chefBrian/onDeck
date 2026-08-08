using System.Collections.Concurrent;

namespace OnDeck.Core.Tests;

/// <summary>
/// A pumping single-threaded <see cref="SynchronizationContext"/> standing in for the WPF
/// <c>Dispatcher</c> that Core runs on in the app. Every continuation and every posted
/// callback runs on the thread that called <see cref="Run"/>, in FIFO order — which is what
/// makes the <c>isStillActive</c> race guard and the coalesced list rebuild deterministic
/// under test.
/// </summary>
internal sealed class SingleThreadedContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
            // The pump has finished; work posted after that is irrelevant to the assertions.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>
    /// Runs <paramref name="body"/> under a fresh context, pumping until it completes.
    /// Exceptions from the body surface to the caller.
    /// </summary>
    public static void Run(Func<Task> body)
    {
        var previous = Current;
        var context = new SingleThreadedContext();
        SetSynchronizationContext(context);

        try
        {
            var task = body();
            task.ContinueWith(
                _ => context._queue.CompleteAdding(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            foreach (var (callback, state) in context._queue.GetConsumingEnumerable()) callback(state);

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Yields repeatedly so queued continuations — and the continuations they queue in turn —
    /// all get pumped before the assertions run. Each yield lets one generation of queued work
    /// run, so a chain like resync → schedule fetch → rebuild needs a couple of dozen.
    /// </summary>
    public static async Task Settle(int rounds = 32)
    {
        for (var i = 0; i < rounds; i++) await Task.Yield();
    }
}
