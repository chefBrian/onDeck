using System.IO;
using Microsoft.Win32;

namespace OnDeck.App.Tray;

/// <summary>
/// Tracks the taskbar's light/dark setting so the tray icon keeps contrast. Windows raises
/// <c>UserPreferenceChanged</c> for this; the registry value is the source of truth.
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public ThemeWatcher()
    {
        SystemUsesLightTheme = ReadTheme("SystemUsesLightTheme");
        AppsUseLightTheme = ReadTheme("AppsUseLightTheme");
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the taskbar is light, which needs the dark icon.</summary>
    public bool SystemUsesLightTheme { get; private set; }

    /// <summary>
    /// True when app surfaces are light. Distinct from <see cref="SystemUsesLightTheme"/> —
    /// "light apps, dark taskbar" is a standard Windows 11 pairing, so driving the flyout's
    /// palette off the taskbar value would give dark text on a dark flyout.
    /// </summary>
    public bool AppsUseLightTheme { get; private set; }

    public event Action? Changed;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

        var system = ReadTheme("SystemUsesLightTheme");
        var apps = ReadTheme("AppsUseLightTheme");
        if (system == SystemUsesLightTheme && apps == AppsUseLightTheme) return;

        SystemUsesLightTheme = system;
        AppsUseLightTheme = apps;
        Changed?.Invoke();
    }

    private static bool ReadTheme(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(valueName) is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;       // assume the dark default
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
