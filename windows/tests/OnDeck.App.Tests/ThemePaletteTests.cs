using System.Windows;
using System.Windows.Media;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void Keys_AreCoveredByBothThemes()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        foreach (var key in ThemePalette.Keys)
        {
            Assert.True(light.Colors.ContainsKey(key), $"light palette is missing {key}");
            Assert.True(dark.Colors.ContainsKey(key), $"dark palette is missing {key}");
        }

        Assert.Equal(ThemePalette.Keys.Count, light.Colors.Count);
        Assert.Equal(ThemePalette.Keys.Count, dark.Colors.Count);
    }

    [Fact]
    public void TextInvertsBetweenThemes()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        // Dark theme wants light text and vice versa - a palette that got this backwards
        // renders the whole flyout unreadable.
        Assert.True(Brightness(light.Colors[ThemePalette.TextPrimary]) < 0.5);
        Assert.True(Brightness(dark.Colors[ThemePalette.TextPrimary]) > 0.5);
    }

    [Fact]
    public void SecondaryTextIsDimmerThanPrimary()
    {
        var dark = ThemePalette.For(appsUseLightTheme: false);

        Assert.True(dark.Colors[ThemePalette.TextSecondary].A < dark.Colors[ThemePalette.TextPrimary].A);
    }

    [Fact]
    public void ApplyTo_PublishesEveryKeyAsABrush()
    {
        var resources = new ResourceDictionary();

        ThemePalette.For(appsUseLightTheme: false).ApplyTo(resources);

        foreach (var key in ThemePalette.Keys)
        {
            var brush = Assert.IsType<SolidColorBrush>(resources[key]);
            Assert.True(brush.IsFrozen);
        }
    }

    [Fact]
    public void ApplyTo_ReplacesAnEarlierPalette()
    {
        var resources = new ResourceDictionary();

        ThemePalette.For(appsUseLightTheme: false).ApplyTo(resources);
        ThemePalette.For(appsUseLightTheme: true).ApplyTo(resources);

        var brush = (SolidColorBrush)resources[ThemePalette.TextPrimary];
        Assert.Equal(
            ThemePalette.For(appsUseLightTheme: true).Colors[ThemePalette.TextPrimary], brush.Color);
    }

    private static double Brightness(Color color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;
}
