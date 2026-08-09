namespace OnDeck.App.Platform;

public enum LaunchAction
{
    /// <summary>Build the tray icon, windows and engine — the normal launch.</summary>
    RunShell,

    /// <summary>
    /// A toast activation that arrived while another instance holds the mutex. Handle the click
    /// and go away without building a second tray icon.
    /// </summary>
    HandleToastActivationAndExit,

    /// <summary>Send one of each notification type so a human can look at them, then exit.</summary>
    SendTestToastsAndExit,

    /// <summary>A duplicate launch: wake the live instance's flyout and exit.</summary>
    SignalExistingAndExit,
}

/// <summary>
/// What a given launch is for. This used to be a single <c>if</c> inside <c>App.OnStartup</c>;
/// toast activation added a case that must not be killed as a duplicate
/// (<c>spikes/ToastActivationSpike/FINDINGS.md</c>, finding 5), and branching inside
/// <c>OnStartup</c> is unreachable from a test.
/// </summary>
public static class StartupPlan
{
    public const string TestToastSwitch = "--test-toast";

    public static bool WantsTestToasts(IEnumerable<string> arguments) =>
        arguments.Any(argument =>
            string.Equals(argument, TestToastSwitch, StringComparison.OrdinalIgnoreCase));

    public static LaunchAction Decide(
        bool acquiredMutex, bool wasToastActivated, bool wantsTestToasts)
    {
        // Diagnostics first: it has to work whether or not the app is already running, which is
        // the normal way to use it.
        if (wantsTestToasts) return LaunchAction.SendTestToastsAndExit;

        if (acquiredMutex) return LaunchAction.RunShell;

        return wasToastActivated
            ? LaunchAction.HandleToastActivationAndExit
            : LaunchAction.SignalExistingAndExit;
    }
}
