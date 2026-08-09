using System.IO;

namespace OnDeck.App.Platform;

/// <summary>
/// A one-file log for things that can only be observed on a running desktop — DWM refusing an
/// attribute, a window landing on the wrong monitor. <c>Debug.WriteLine</c> is invisible outside
/// a debugger, and these are exactly the questions that come up when nobody is attached.
/// </summary>
public static class ShellLog
{
    private static readonly Lock Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "onDeck",
        "shell.log");

    public static void Append(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never take the app down.
            }
        }
    }
}
