namespace OnDeck.App.Tray;

public enum TrayIcon
{
    White,
    Dark,
    Green,
}

/// <summary>
/// The macOS menu bar draws a template image the system recolours; Windows has no equivalent,
/// so the shell picks an asset. Green means at least one player is active — the whole point of
/// the app — and outranks taskbar contrast.
/// </summary>
public static class TrayIconVariant
{
    public static TrayIcon Select(bool systemUsesLightTheme, bool hasActivePlayers) =>
        hasActivePlayers ? TrayIcon.Green
        : systemUsesLightTheme ? TrayIcon.Dark
        : TrayIcon.White;

    public static string ResourcePath(TrayIcon icon) => icon switch
    {
        TrayIcon.Dark => "pack://application:,,,/Assets/tray-dark.ico",
        TrayIcon.Green => "pack://application:,,,/Assets/tray-green.ico",
        _ => "pack://application:,,,/Assets/tray-white.ico",
    };
}
