using System.IO;

namespace OnDeck.Spike;

/// <summary>
/// Appends to a file rather than only the window, because the interesting case — clicking a
/// toast while the app is dead — starts a brand new process. The file is the evidence that
/// survives across those launches.
/// </summary>
internal static class SpikeLog
{
    private static readonly Lock Gate = new();

    public static string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "ondeck-spike");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "activation.log");

    public static void Append(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId,-6} {message}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the spike down.
            }
        }
    }

    public static string ReadAll()
    {
        try
        {
            return File.Exists(LogPath) ? File.ReadAllText(LogPath) : "(no log yet)";
        }
        catch (IOException ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
            }
            catch (IOException)
            {
                // Ignored.
            }
        }
    }
}
