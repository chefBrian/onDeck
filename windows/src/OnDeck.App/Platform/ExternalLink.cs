using System.Diagnostics;

namespace OnDeck.App.Platform;

/// <summary>Hands a URL to the default browser. Never throws — a dead link must not kill the app.</summary>
public static class ExternalLink
{
    public static void Open(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShellLog.Append($"[Link] failed to open {url}: {exception.Message}");
        }
    }
}
