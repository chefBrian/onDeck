using OnDeck.App.Tray;

namespace OnDeck.App.Tests;

public class TrayIconVariantTests
{
    [Theory]
    [InlineData(false, false, TrayIcon.White)]   // dark taskbar, idle
    [InlineData(true, false, TrayIcon.Dark)]     // light taskbar, idle
    [InlineData(false, true, TrayIcon.Green)]    // active wins over theme
    [InlineData(true, true, TrayIcon.Green)]
    public void Select_PrefersActiveThenContrastsWithTheTaskbar(
        bool systemUsesLightTheme, bool hasActivePlayers, TrayIcon expected)
    {
        Assert.Equal(expected, TrayIconVariant.Select(systemUsesLightTheme, hasActivePlayers));
    }

    [Theory]
    [InlineData(TrayIcon.White, "tray-white.ico")]
    [InlineData(TrayIcon.Dark, "tray-dark.ico")]
    [InlineData(TrayIcon.Green, "tray-green.ico")]
    public void ResourcePath_PointsAtTheGeneratedAsset(TrayIcon icon, string file)
    {
        Assert.EndsWith(file, TrayIconVariant.ResourcePath(icon));
        Assert.StartsWith("pack://application:,,,/", TrayIconVariant.ResourcePath(icon));
    }
}
