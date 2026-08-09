using Microsoft.Win32;
using OnDeck.App.Platform;

namespace OnDeck.App.Tests;

public class StartupManagerTests : IDisposable
{
    // Deliberately NOT the real Run key. A test that writes there adds a genuine startup entry
    // to whoever's machine is running the suite.
    private readonly string _keyPath = @"Software\onDeck\StartupTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    private StartupManager Manager(string exe = @"C:\Apps\onDeck\onDeck.exe") => new(_keyPath, exe);

    [Fact]
    public void TheDefaultKeyIsTheRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupManager.DefaultKeyPath);
        Assert.Equal("onDeck", StartupManager.ValueName);
    }

    [Fact]
    public void ItIsOffUntilItIsTurnedOn()
    {
        Assert.False(Manager().IsEnabled);
    }

    [Fact]
    public void EnablingWritesTheQuotedExePath()
    {
        var manager = Manager();

        manager.SetEnabled(true);

        Assert.True(manager.IsEnabled);
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);

        // Quoted: Windows parses an unquoted path at the first space, so
        // C:\Program Files\... would launch "C:\Program" with "Files\..." as an argument.
        Assert.Equal(@"""C:\Apps\onDeck\onDeck.exe""", key!.GetValue(StartupManager.ValueName));
    }

    [Fact]
    public void DisablingRemovesTheValue()
    {
        var manager = Manager();
        manager.SetEnabled(true);

        manager.SetEnabled(false);

        Assert.False(manager.IsEnabled);
    }

    [Fact]
    public void DisablingWhenItWasNeverOnIsHarmless()
    {
        Manager().SetEnabled(false);

        Assert.False(Manager().IsEnabled);
    }

    [Fact]
    public void EnablingTwiceLeavesOneValue()
    {
        var manager = Manager();

        manager.SetEnabled(true);
        manager.SetEnabled(true);

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.Equal(new[] { StartupManager.ValueName }, key!.GetValueNames());
    }

    [Fact]
    public void AValueForThisExeIsCurrent()
    {
        var manager = Manager();
        manager.SetEnabled(true);

        Assert.True(manager.IsCurrent);
    }

    [Fact]
    public void AValueLeftBehindByAnOlderLocationIsNotCurrent()
    {
        // Debug, Release and a published copy all live at different paths, and Windows silently
        // fails to start a program that isn't there.
        new StartupManager(_keyPath, @"C:\Old\onDeck.exe").SetEnabled(true);

        var moved = new StartupManager(_keyPath, @"C:\New\onDeck.exe");

        Assert.True(moved.IsEnabled);
        Assert.False(moved.IsCurrent);
    }

    [Fact]
    public void ReEnablingAfterAMoveRewritesThePath()
    {
        new StartupManager(_keyPath, @"C:\Old\onDeck.exe").SetEnabled(true);
        var moved = new StartupManager(_keyPath, @"C:\New\onDeck.exe");

        moved.SetEnabled(true);

        Assert.True(moved.IsCurrent);
    }

    [Fact]
    public void NothingIsWrittenUntilSomethingIsEnabled()
    {
        _ = Manager().IsEnabled;
        _ = Manager().IsCurrent;

        Assert.Null(Registry.CurrentUser.OpenSubKey(_keyPath));
    }
}
