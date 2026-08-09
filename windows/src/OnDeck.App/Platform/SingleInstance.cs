namespace OnDeck.App.Platform;

/// <summary>
/// One tray icon per user session. The second launch signals the first and exits; the first
/// responds by opening its flyout, which is what a user double-clicking the exe expects.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\onDeck.singleInstance";
    private const string SignalName = @"Local\onDeck.showFlyout";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal, (_, _) => SecondInstanceStarted?.Invoke(), null, Timeout.Infinite, false);
    }

    /// <summary>Raised on a thread-pool thread when another launch signals us.</summary>
    public event Action? SecondInstanceStarted;

    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(SignalName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance exited between our mutex check and here. Nothing to signal.
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _signal.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
