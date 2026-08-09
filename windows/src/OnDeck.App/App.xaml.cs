using System.Net.Http;
using System.Windows;
using OnDeck.App.Notifications;
using OnDeck.App.Platform;
using OnDeck.App.Tray;
using OnDeck.App.Views;
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
    private ThemeWatcher? _theme;
    private FlyoutWindow? _flyout;
    private FloatingPanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private TeamLogoStore? _logos;
    private SettingsStore? _settingsStore;
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
        _settingsStore = settings;
        var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 4 });
        var mlb = new MlbStatsApi(http);
        var fantrax = new FantraxApi(http);
        var headshots = new HeadshotCache(http, HeadshotCache.DefaultCacheDirectory());
        _logos = new TeamLogoStore(new TeamLogoCache(http, TeamLogoCache.DefaultCacheDirectory()));

        _orchestrator = new AppOrchestrator(
            new RosterManager(fantrax, mlb, settings, headshots),
            new ScheduleManager(mlb),
            new GameMonitor(mlb),
            new StateManager(),
            fantrax,
            settings,
            new ToastService(settings, headshots, new WindowsToastPresenter()));

        _theme = new ThemeWatcher();
        _theme.Changed += ApplyPalette;
        ApplyPalette();

        _tray = new TrayIconService(_orchestrator);
        _tray.OpenRequested += () => OpenFlyout(TrayGeometry.CursorPosition());
        _tray.SettingsRequested += OpenSettings;
        _tray.RefreshRequested += () => _ = _orchestrator.ResyncRosterAsync();
        _tray.QuitRequested += Shutdown;

        // The flyout is constructed first so the panel's OpenChanged handler closes over a
        // non-null _flyout: IsVisibleChanged can fire during the Toggle() two lines below.
        _flyout = new FlyoutWindow(_orchestrator, _logos);
        _flyout.FloatRequested += ToggleFloat;
        _flyout.SettingsRequested += OpenSettings;
        _tray.FloatRequested += ToggleFloat;

        _panel = new FloatingPanelWindow(_orchestrator, _logos, settings);
        _panel.OpenChanged += () => _flyout.SetFloating(_panel.IsOpen);

        if (settings.AlwaysOpenPopout) _panel.Toggle();

        _ = _orchestrator.StartAsync();
    }

    /// <summary>
    /// Republishes the <c>OnDeck.*</c> brushes for the current app theme. Every window binds
    /// them with <c>DynamicResource</c>, so a live theme change repaints in place.
    /// </summary>
    private void ApplyPalette() => ThemePalette.For(_theme!.AppsUseLightTheme).ApplyTo(Resources);

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

    private void ToggleFloat()
    {
        _flyout?.Hide();
        _panel?.Toggle();
    }

    /// <summary>
    /// One window at a time, released when it closes. macOS flips the activation policy back to
    /// <c>.accessory</c> on dismissal so the OS can unload the Settings infrastructure
    /// (<c>SettingsView.swift:118-122</c>); Windows has no equivalent, so the closest thing is to
    /// actually let the window go and rebuild it on the next request.
    /// </summary>
    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_orchestrator!, _settingsStore!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _panel?.Close();
        _settingsWindow?.Close();
        _tray?.Dispose();
        _theme?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
