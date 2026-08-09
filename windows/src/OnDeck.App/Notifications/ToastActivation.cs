using Microsoft.Toolkit.Uwp.Notifications;

namespace OnDeck.App.Notifications;

/// <summary>
/// The click URL's round trip through a toast's argument string. <c>PORT_PLAN.md</c>: a toast
/// click opens its link — the stream for batting/pitching/results, the Fantrax page for
/// not-in-lineup — rather than merely foregrounding the app.
/// </summary>
public static class ToastActivation
{
    public const string UrlKey = "url";

    /// <summary>The argument to attach to a toast, or null when there is nothing to open.</summary>
    public static string? Argument(Uri? url) =>
        url is null ? null : new ToastArguments().Add(UrlKey, url.AbsoluteUri).ToString();

    /// <summary>
    /// The URL to open for an activation, or null if there isn't a usable one. Never throws: this
    /// runs on an OS callback, and a malformed argument must not take the process down.
    /// </summary>
    public static Uri? UrlFrom(string? argument)
    {
        if (string.IsNullOrEmpty(argument)) return null;

        try
        {
            var arguments = ToastArguments.Parse(argument);
            if (!arguments.Contains(UrlKey)) return null;
            if (!Uri.TryCreate(arguments[UrlKey], UriKind.Absolute, out var url)) return null;

            // This string arrives from outside the process and ends up at ShellExecute, which
            // launches any registered protocol handler. We only ever write http(s).
            return url.Scheme is "http" or "https" ? url : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
