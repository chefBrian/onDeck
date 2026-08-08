using System.Windows;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;

namespace OnDeck.Spike;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        App.ActivationRecorded += ReloadLog;
        Closed += (_, _) => App.ActivationRecorded -= ReloadLog;

        StatusText.Text = ToastNotificationManagerCompat.WasCurrentProcessToastActivated()
            ? "This process was launched BY a toast click."
            : "Started normally.";

        ReloadLog();
    }

    private void SendToast(object sender, RoutedEventArgs e) => Send();

    private void SendToastAndQuit(object sender, RoutedEventArgs e)
    {
        Send();
        StatusText.Text = "Toast sent. Quitting in 3s — then click the toast in the Action Center.";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SpikeLog.Append("EXIT    quitting so the next activation lands in a cold process");
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void Send()
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "viewStream")
                .AddArgument("url", "https://www.mlb.com/tv/g776543")
                .AddText("Mookie Betts is batting")
                .AddText("Giants 1 - Dodgers 2, Bot 3")
                .Show();

            SpikeLog.Append("SENT    toast with argument action=viewStream;url=https://www.mlb.com/tv/g776543");
        }
        catch (Exception ex)
        {
            SpikeLog.Append($"FAILED  could not send toast: {ex.GetType().Name}: {ex.Message}");
            StatusText.Text = $"Send failed: {ex.Message}";
        }

        ReloadLog();
    }

    private void RefreshLog(object sender, RoutedEventArgs e) => ReloadLog();

    private void ClearLog(object sender, RoutedEventArgs e)
    {
        SpikeLog.Clear();
        ReloadLog();
    }

    private void UninstallRegistration(object sender, RoutedEventArgs e)
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            StatusText.Text = "Toast registration removed (Start Menu shortcut + COM activator).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Uninstall failed: {ex.Message}";
        }
    }

    private void ReloadLog()
    {
        LogText.Text = SpikeLog.ReadAll();
        Title = $"onDeck — toast activation spike  ({SpikeLog.LogPath})";
    }
}
