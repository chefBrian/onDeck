using System.Net.Http;
using System.Windows;
using OnDeck.App.Notifications;
using OnDeck.App.Platform;
using OnDeck.App.Tray;
using OnDeck.App.Windows;
using OnDeck.Core;
using OnDeck.Core.Managers;
using OnDeck.Core.Networking;
using OnDeck.Core.Utilities;

namespace OnDeck.App;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TrayIconService? _tray;
    private FlyoutWindow? _flyout;
    private AppOrchestrator? _orchestrator;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstance.TryAcquire(out var instance))
        {
            // Already running: hand the click to the live instance instead of adding a second
            // tray icon. A -ToastActivated launch never lands here - COM routes those to the
            // running process, which already holds the mutex.
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _singleInstance = instance;
        _singleInstance!.SecondInstanceStarted += () => Dispatcher.Invoke(() => OpenFlyout(null));

        base.OnStartup(e);

        // Everything below runs on the Dispatcher thread, which is the point: AppOrchestrator
        // captures this SynchronizationContext and posts its coalesced rebuilds back to it.
        var settings = new SettingsStore();
        var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 4 });
        var mlb = new MlbStatsApi(http);
        var fantrax = new FantraxApi(http);
        var headshots = new HeadshotCache(http, HeadshotCache.DefaultCacheDirectory());

        _orchestrator = new AppOrchestrator(
            new RosterManager(fantrax, mlb, settings, headshots),
            new ScheduleManager(mlb),
            new GameMonitor(mlb),
            new StateManager(),
            fantrax,
            settings,
            new LoggingNotificationSink());

        _tray = new TrayIconService(_orchestrator);
        _tray.OpenRequested += () => OpenFlyout(TrayGeometry.CursorPosition());
        _tray.RefreshRequested += () => _ = _orchestrator.ResyncRosterAsync();
        _tray.QuitRequested += Shutdown;

        _flyout = new FlyoutWindow(_orchestrator);

        _ = _orchestrator.StartAsync();
    }

    private void OpenFlyout(Point? anchorDevicePixels)
    {
        if (_flyout is null) return;

        if (_flyout.IsVisible)
        {
            _flyout.Hide();
            return;
        }

        _flyout.ShowAt(anchorDevicePixels);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
