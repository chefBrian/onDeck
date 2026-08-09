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
        SystemUsesLightTheme = ReadSystemUsesLightTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the taskbar is light, which needs the dark icon.</summary>
    public bool SystemUsesLightTheme { get; private set; }

    public event Action? Changed;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;

        var current = ReadSystemUsesLightTheme();
        if (current == SystemUsesLightTheme) return;

        SystemUsesLightTheme = current;
        Changed?.Invoke();
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;       // assume the dark taskbar default
        }
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
