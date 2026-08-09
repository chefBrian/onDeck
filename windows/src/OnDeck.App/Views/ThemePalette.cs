using System.Windows;
using System.Windows.Media;

namespace OnDeck.App.Views;

/// <summary>
/// The semantic colours <c>Views/MenuBarView.swift</c> uses, resolved for the current Windows
/// app theme and published as frozen brushes under <c>OnDeck.*</c> resource keys.
/// <para>
/// WPF's own Fluent keys are deliberately not used: a <c>DynamicResource</c> naming a key that
/// isn't there resolves to null and renders nothing, with no build or runtime error. Owning the
/// keys makes a missing colour impossible rather than invisible.
/// </para>
/// </summary>
public sealed record ThemePalette
{
    public const string TextPrimary = "OnDeck.Text.Primary";
    public const string TextSecondary = "OnDeck.Text.Secondary";
    public const string Divider = "OnDeck.Divider";
    public const string RowHover = "OnDeck.Row.Hover";
    public const string Green = "OnDeck.Accent.Green";
    public const string Orange = "OnDeck.Accent.Orange";
    public const string Red = "OnDeck.Accent.Red";
    public const string Blue = "OnDeck.Accent.Blue";
    public const string BaseOccupied = "OnDeck.Base.Occupied";
    public const string BaseEmpty = "OnDeck.Base.Empty";
    public const string Surface = "OnDeck.Surface";
    public const string SurfaceCard = "OnDeck.Surface.Card";

    public static IReadOnlyList<string> Keys { get; } =
    [
        TextPrimary, TextSecondary, Divider, RowHover,
        Green, Orange, Red, Blue, BaseOccupied, BaseEmpty,
        Surface, SurfaceCard,
    ];

    private ThemePalette(IReadOnlyDictionary<string, Color> colors) => Colors = colors;

    public IReadOnlyDictionary<string, Color> Colors { get; }

    public static ThemePalette For(bool appsUseLightTheme) =>
        appsUseLightTheme ? Light() : Dark();

    /// <summary>
    /// Publishes every colour as a frozen <see cref="SolidColorBrush"/>, replacing any palette
    /// already there so a live theme change repaints without rebuilding the visual tree.
    /// </summary>
    public void ApplyTo(ResourceDictionary resources)
    {
        foreach (var (key, color) in Colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static ThemePalette Dark() => new(new Dictionary<string, Color>
    {
        [TextPrimary] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        [TextSecondary] = Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF),   // SwiftUI .secondary
        [Divider] = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF),         // SwiftUI .quaternary
        [RowHover] = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),        // .white.opacity(0.1)
        [Green] = Color.FromArgb(0xFF, 0x32, 0xD7, 0x4B),
        [Orange] = Color.FromArgb(0xFF, 0xFF, 0x9F, 0x0A),
        [Red] = Color.FromArgb(0xFF, 0xFF, 0x45, 0x3A),
        [Blue] = Color.FromArgb(0xFF, 0x0A, 0x84, 0xFF),
        [BaseOccupied] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),       // .gray.opacity(0.3)

        // The settings window's grouped sections - SwiftUI's .formStyle(.grouped) recesses the
        // window and raises the cards. Opaque: a window background with alpha shows whatever
        // the compositor left behind it.
        [Surface] = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
        [SurfaceCard] = Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B),
    });

    private static ThemePalette Light() => new(new Dictionary<string, Color>
    {
        [TextPrimary] = Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        [TextSecondary] = Color.FromArgb(0x8C, 0x00, 0x00, 0x00),
        [Divider] = Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        [RowHover] = Color.FromArgb(0x14, 0x00, 0x00, 0x00),
        [Green] = Color.FromArgb(0xFF, 0x1D, 0x8A, 0x3D),
        [Orange] = Color.FromArgb(0xFF, 0xB2, 0x50, 0x00),
        [Red] = Color.FromArgb(0xFF, 0xD7, 0x00, 0x15),
        [Blue] = Color.FromArgb(0xFF, 0x00, 0x40, 0xDD),
        [BaseOccupied] = Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E),
        [BaseEmpty] = Color.FromArgb(0x4D, 0x80, 0x80, 0x80),
        [Surface] = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF7),
        [SurfaceCard] = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
    });
}
