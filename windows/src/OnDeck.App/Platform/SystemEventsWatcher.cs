using Microsoft.Win32;

namespace OnDeck.App.Platform;

/// <summary>
/// Port of <c>setupSystemResumeHandler</c> (<c>AppState.swift:524-539</c>): listens for the
/// machine waking and for our session coming back, and calls back so the shell can ask Core to
/// recover. <see cref="SystemResumeTrigger"/> owns which events qualify.
/// <para>
/// <b>The callback runs on a background thread.</b> Whatever it does must marshal to the
/// Dispatcher before touching Core.
/// </para>
/// </summary>
public sealed class SystemEventsWatcher : IDisposable
{
    private readonly Action<string> _onResume;
    private readonly PowerModeChangedEventHandler _power;
    private readonly SessionSwitchEventHandler _session;

    public SystemEventsWatcher(Action<string> onResume)
    {
        _onResume = onResume;

        _power = (_, e) =>
        {
            if (SystemResumeTrigger.IsResume(e.Mode)) Raise($"power:{e.Mode}");
        };

        _session = (_, e) =>
        {
            if (SystemResumeTrigger.IsResume(e.Reason)) Raise($"session:{e.Reason}");
        };

        SystemEvents.PowerModeChanged += _power;
        SystemEvents.SessionSwitch += _session;

        ShellLog.Append("[System] resume observers registered (power, session)");
    }

    private void Raise(string reason)
    {
        // The only evidence these fire at all: nobody is attached to a debugger when the lid
        // closes. Swift logs the same thing at .notice.
        ShellLog.Append($"[System] resume: {reason}");
        _onResume(reason);
    }

    public void Dispose()
    {
        // SystemEvents keeps its subscriber list for the life of the process, so a handler that
        // is never removed pins this object and everything its callback closes over.
        SystemEvents.PowerModeChanged -= _power;
        SystemEvents.SessionSwitch -= _session;
    }
}
