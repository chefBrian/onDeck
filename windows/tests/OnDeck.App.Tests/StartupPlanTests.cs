using OnDeck.App.Platform;

namespace OnDeck.App.Tests;

public class StartupPlanTests
{
    [Fact]
    public void AnOrdinaryFirstLaunchRunsTheShell()
    {
        var action = StartupPlan.Decide(
            acquiredMutex: true, wasToastActivated: false, wantsTestToasts: false);

        Assert.Equal(LaunchAction.RunShell, action);
    }

    [Fact]
    public void AColdToastActivationRunsTheShell()
    {
        // The app was dead, Windows started it with -ToastActivated -Embedding. It should
        // become the app - the activation arrives a beat later and is handled in-process.
        var action = StartupPlan.Decide(
            acquiredMutex: true, wasToastActivated: true, wantsTestToasts: false);

        Assert.Equal(LaunchAction.RunShell, action);
    }

    [Fact]
    public void ASecondLaunchSignalsTheLiveInstance()
    {
        var action = StartupPlan.Decide(
            acquiredMutex: false, wasToastActivated: false, wantsTestToasts: false);

        Assert.Equal(LaunchAction.SignalExistingAndExit, action);
    }

    [Fact]
    public void AToastActivationThatRacesTheLiveInstanceIsHandledNotKilled()
    {
        // Spike finding 5. Shutting this down before the activation is delivered loses the
        // click, and the user just sees a toast that did nothing.
        var action = StartupPlan.Decide(
            acquiredMutex: false, wasToastActivated: true, wantsTestToasts: false);

        Assert.Equal(LaunchAction.HandleToastActivationAndExit, action);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TestToastsOutrankEverythingElse(bool acquiredMutex, bool wasToastActivated)
    {
        // It has to work while the app is running, which is the normal way to use it.
        var action = StartupPlan.Decide(acquiredMutex, wasToastActivated, wantsTestToasts: true);

        Assert.Equal(LaunchAction.SendTestToastsAndExit, action);
    }

    [Fact]
    public void TheTestToastSwitchIsRecognised()
    {
        Assert.True(StartupPlan.WantsTestToasts(["--test-toast"]));
        Assert.True(StartupPlan.WantsTestToasts(["--Test-Toast"]));
        Assert.True(StartupPlan.WantsTestToasts(["-ToastActivated", "--test-toast"]));
    }

    [Fact]
    public void OtherArgumentsAreNotTheTestSwitch()
    {
        Assert.False(StartupPlan.WantsTestToasts([]));
        Assert.False(StartupPlan.WantsTestToasts(["-ToastActivated", "-Embedding"]));
        Assert.False(StartupPlan.WantsTestToasts(["--test-toasts"]));
    }
}
