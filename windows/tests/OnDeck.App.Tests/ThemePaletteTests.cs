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

    [Fact]
    public void BackdropTintFollowsTheSurfaceColour()
    {
        // ABGR, not ARGB: the accent policy's GradientColor swaps the byte order. A swap is
        // invisible on the grey dark surface (B == R), so the light surface #F2F2F7 is the one
        // that catches it: blue must land in the high byte.
        Assert.Equal(0x0D202020u, ThemePalette.For(appsUseLightTheme: false).BackdropTintAbgr);
        Assert.Equal(0x0DF7F2F2u, ThemePalette.For(appsUseLightTheme: true).BackdropTintAbgr);
    }

    [Fact]
    public void ApplyTo_PublishesTheBackdropTint()
    {
        var resources = new ResourceDictionary();

        ThemePalette.For(appsUseLightTheme: true).ApplyTo(resources);

        // The windows re-read this at RefreshBackdrop time, so a live theme change retints
        // the acrylic without anyone re-plumbing a value to them.
        Assert.Equal(0x0DF7F2F2u, Assert.IsType<uint>(resources[ThemePalette.BackdropTint]));
    }

    [Fact]
    public void VeilIsATranslucentSurface()
    {
        foreach (var light in new[] { true, false })
        {
            var palette = ThemePalette.For(light);
            var veil = palette.Colors[ThemePalette.BackdropVeil];
            var surface = palette.Colors[ThemePalette.Surface];

            // The veil is the app-side darkening laid over the accent blur - the acrylic look
            // with the darkness under our control instead of the OS material's. It must carry
            // the surface's hue and real translucency, or the blur underneath is pointless.
            Assert.Equal(0x8C, veil.A);
            Assert.Equal((surface.R, surface.G, surface.B), (veil.R, veil.G, veil.B));
        }
    }

    [Fact]
    public void SurfacesAreFullyOpaque()
    {
        foreach (var palette in new[]
                 {
                     ThemePalette.For(appsUseLightTheme: true),
                     ThemePalette.For(appsUseLightTheme: false),
                 })
        {
            // A window background with any transparency shows whatever the compositor left
            // behind it. The flyout can afford alpha; a real window cannot.
            Assert.Equal(0xFF, palette.Colors[ThemePalette.Surface].A);
            Assert.Equal(0xFF, palette.Colors[ThemePalette.SurfaceCard].A);
        }
    }

    [Fact]
    public void CardsSitAboveTheSurfaceInBothThemes()
    {
        foreach (var palette in new[]
                 {
                     ThemePalette.For(appsUseLightTheme: true),
                     ThemePalette.For(appsUseLightTheme: false),
                 })
        {
            // Grouped-form cards read as raised. If this inverts, the sections vanish into the
            // background and the window looks like an undifferentiated list.
            Assert.True(
                Brightness(palette.Colors[ThemePalette.SurfaceCard])
                > Brightness(palette.Colors[ThemePalette.Surface]));
        }
    }

    [Fact]
    public void TextReadsAgainstTheCardItSitsOn()
    {
        var light = ThemePalette.For(appsUseLightTheme: true);
        var dark = ThemePalette.For(appsUseLightTheme: false);

        Assert.True(Brightness(light.Colors[ThemePalette.SurfaceCard]) > 0.5);
        Assert.True(Brightness(light.Colors[ThemePalette.TextPrimary]) < 0.5);

        Assert.True(Brightness(dark.Colors[ThemePalette.SurfaceCard]) < 0.5);
        Assert.True(Brightness(dark.Colors[ThemePalette.TextPrimary]) > 0.5);
    }

    private static double Brightness(Color color) =>
        ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255.0;
}
