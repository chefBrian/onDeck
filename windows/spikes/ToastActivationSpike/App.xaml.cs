using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace OnDeck.Spike;

public partial class App : Application
{
    /// <summary>Raised on the UI thread after a toast activation is recorded.</summary>
    public static event Action? ActivationRecorded;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Must be wired before anything else: when the app is launched *by* a toast click the
        // activation fires almost immediately after startup.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;

        var wasToastActivated = ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        SpikeLog.Append(
            $"START   wasToastActivated={wasToastActivated} argv=[{string.Join(" ", e.Args)}]");

        base.OnStartup(e);
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        // Fires on a background thread.
        var parsed = DescribeArguments(e.Argument);
        SpikeLog.Append($"ACTIVATED argument=\"{e.Argument}\" parsed={parsed}");

        if (e.UserInput is { Count: > 0 } input)
        {
            SpikeLog.Append($"          userInput={{{string.Join(", ", input.Select(pair => $"{pair.Key}={pair.Value}"))}}}");
        }

        Dispatcher.Invoke(() =>
        {
            ActivationRecorded?.Invoke();

            // Bring the (possibly brand new) window forward so a click visibly does something.
            if (Current.MainWindow is { } window)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Show();
                window.Activate();
            }
        });
    }

    private static string DescribeArguments(string argument)
    {
        try
        {
            var arguments = ToastArguments.Parse(argument);
            var action = arguments.Contains("action") ? arguments["action"] : "(none)";
            var url = arguments.Contains("url") ? arguments["url"] : "(none)";
            return $"{{action={action}, url={url}}}";
        }
        catch (Exception ex)
        {
            return $"(unparseable: {ex.GetType().Name})";
        }
    }
}
