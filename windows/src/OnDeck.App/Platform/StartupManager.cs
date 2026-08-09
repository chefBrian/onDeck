using System.IO;
using Microsoft.Win32;

namespace OnDeck.App.Platform;

/// <summary>
/// Launch at login: one value under the current user's <c>Run</c> key. Default off
/// (<c>PORT_PLAN.md</c> Decision 3).
/// <para>
/// Deliberately outside <see cref="OnDeck.Core.ISettingsStore"/> — <c>PORT_PLAN.md</c> scopes
/// launch-at-login as shell-only, like the floating-panel frame. The truth lives in the registry:
/// a user can remove the entry from Task Manager's Startup tab, and a copy in
/// <c>settings.json</c> would then be lying. Every read goes to the registry.
/// </para>
/// <para>
/// The macOS app has no counterpart — this is a Windows-only addition.
/// </para>
/// </summary>
public sealed class StartupManager(string? keyPath = null, string? executablePath = null)
{
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "onDeck";

    private readonly string _keyPath = keyPath ?? DefaultKeyPath;

    private readonly string _executablePath = executablePath ?? Environment.ProcessPath ?? "";

    /// <summary>
    /// The value Windows runs. Quoted: an unquoted path is parsed at the first space, so
    /// <c>C:\Program Files\onDeck\onDeck.exe</c> would try to launch <c>C:\Program</c>.
    /// </summary>
    public string CommandLine => $"\"{_executablePath}\"";

    public bool IsEnabled => Read() is not null;

    /// <summary>
    /// Whether the stored command points at <em>this</em> exe. False after the exe moves — Debug,
    /// Release and a published copy all live at different paths, and Windows silently fails to
    /// start a program that isn't there.
    /// </summary>
    public bool IsCurrent => string.Equals(Read(), CommandLine, StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            ShellLog.Append($"[Startup] launch at login {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or System.Security.SecurityException)
        {
            // Group policy can lock the Run key. The checkbox re-reads the registry on the next
            // render, so it corrects itself rather than lying about what happened.
            ShellLog.Append($"[Startup] could not write the Run value: {exception.Message}");
        }
    }

    private string? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or System.Security.SecurityException)
        {
            return null;
        }
    }
}
