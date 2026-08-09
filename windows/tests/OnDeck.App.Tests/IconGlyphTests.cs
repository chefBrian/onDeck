using System.Reflection;
using OnDeck.App.Views;

namespace OnDeck.App.Tests;

/// <summary>
/// The icon constants are Segoe Fluent Icons private-use characters, which most editors render as
/// nothing at all — FooterBar's once shipped silently stripped to <c>""</c>, which blanked the
/// Float button and made Refresh vanish on first click. Pin every glyph constant to an actual
/// icon-font character.
/// </summary>
public class IconGlyphTests
{
    [Theory]
    [InlineData(typeof(FooterBar))]
    [InlineData(typeof(FlyoutContent))]
    public void GlyphConstantsAreSingleIconFontCharacters(Type view)
    {
        var glyphs = view
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("GlyphText", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(glyphs);
        Assert.All(glyphs, field =>
        {
            var text = Assert.IsType<string>(field.GetRawConstantValue());
            var ch = Assert.Single(text);
            Assert.InRange(ch, '\uE000', '\uF8FF');
        });
    }
}
