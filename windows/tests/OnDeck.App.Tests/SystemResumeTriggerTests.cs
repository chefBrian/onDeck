using Microsoft.Win32;
using OnDeck.App.Platform;

namespace OnDeck.App.Tests;

public class SystemResumeTriggerTests
{
    [Fact]
    public void WakingFromSleepIsAResume()
    {
        Assert.True(SystemResumeTrigger.IsResume(PowerModes.Resume));
    }

    [Fact]
    public void GoingToSleepAndBatteryChangesAreNot()
    {
        Assert.False(SystemResumeTrigger.IsResume(PowerModes.Suspend));
        Assert.False(SystemResumeTrigger.IsResume(PowerModes.StatusChange));
    }

    [Fact]
    public void ExactlyTheReturningSessionEventsCount()
    {
        // Exhaustive on purpose. Too few and the app comes back from sleep with stale data and
        // no error; too many and every screen lock kicks off a roster resync, a schedule
        // refetch and a monitoring restart.
        var resumes = Enum.GetValues<SessionSwitchReason>()
            .Where(SystemResumeTrigger.IsResume)
            .OrderBy(reason => reason.ToString(), StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                SessionSwitchReason.ConsoleConnect,
                SessionSwitchReason.RemoteConnect,
                SessionSwitchReason.SessionUnlock,
            },
            resumes);
    }

    [Fact]
    public void LeavingIsNeverAResume()
    {
        Assert.False(SystemResumeTrigger.IsResume(SessionSwitchReason.SessionLock));
        Assert.False(SystemResumeTrigger.IsResume(SessionSwitchReason.SessionLogoff));
        Assert.False(SystemResumeTrigger.IsResume(SessionSwitchReason.ConsoleDisconnect));
        Assert.False(SystemResumeTrigger.IsResume(SessionSwitchReason.RemoteDisconnect));
    }

    [Fact]
    public void LoggingOnIsNotAResume()
    {
        // A process already running in the session cannot observe its own logon; treating it as
        // a resume would only fire on someone else's.
        Assert.False(SystemResumeTrigger.IsResume(SessionSwitchReason.SessionLogon));
    }
}
