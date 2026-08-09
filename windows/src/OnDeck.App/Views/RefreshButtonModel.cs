namespace OnDeck.App.Views;

public enum RefreshButtonState
{
    Idle,
    Spinning,
    Done,
    Failed,
}

/// <summary>
/// The Refresh button's four states, ported from <c>FooterButtons.refreshButton</c> in
/// <c>Views/MenuBarView.swift</c>: spin during the sync, show the outcome for 1.2 s, then go
/// back to idle. Clicks during any of that are dropped — Swift's <c>guard state == .idle</c> —
/// so an impatient double-click can't fire two roster syncs.
/// </summary>
public sealed class RefreshButtonModel(TimeProvider? time = null)
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromSeconds(1.2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public RefreshButtonState State { get; private set; } = RefreshButtonState.Idle;

    public event Action? Changed;

    public async Task ClickAsync(Func<Task<bool>> resync)
    {
        if (State != RefreshButtonState.Idle) return;

        Transition(RefreshButtonState.Spinning);

        bool success;
        try
        {
            success = await resync();
        }
        catch (Exception)
        {
            // A throwing sync is a failed sync; the button must not stick on the spinner.
            success = false;
        }

        Transition(success ? RefreshButtonState.Done : RefreshButtonState.Failed);

        await Task.Delay(HoldDuration, _time);

        Transition(RefreshButtonState.Idle);
    }

    private void Transition(RefreshButtonState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
