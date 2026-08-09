using Microsoft.Win32;

namespace OnDeck.App.Platform;

/// <summary>
/// Which Windows system events count as "we've been away — recover". The port of the three
/// observers <c>AppState.swift:524-539</c> registers: <c>didWakeNotification</c>,
/// <c>sessionDidBecomeActiveNotification</c> and <c>com.apple.screenIsUnlocked</c>.
/// <para>
/// Kept apart from <see cref="SystemEventsWatcher"/> because <c>SystemEvents</c> is static and
/// cannot be raised from a test, while getting this set wrong fails silently in both directions:
/// too few and the app returns from sleep with stale data, too many and every screen lock starts
/// a roster resync, a schedule refetch and a monitoring restart.
/// </para>
/// </summary>
public static class SystemResumeTrigger
{
    public static bool IsResume(PowerModes mode) => mode == PowerModes.Resume;

    public static bool IsResume(SessionSwitchReason reason) => reason is
        SessionSwitchReason.SessionUnlock       // com.apple.screenIsUnlocked
        or SessionSwitchReason.ConsoleConnect   // sessionDidBecomeActive - fast user switch back
        or SessionSwitchReason.RemoteConnect;   // ... or an RDP reattach
}
