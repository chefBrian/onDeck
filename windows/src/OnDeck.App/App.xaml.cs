using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
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
    private StartupManager? _startup;
    private AppOrchestrator? _orchestrator;
    private SystemEventsWatcher? _systemEvents;
    private bool _exitAfterActivation;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Wired before anything else: a toast-activated cold start fires this within ~200 ms of
        // startup (spike FINDINGS.md). The event takes the library's own OnActivated delegate,
        // so this must be a lambda - an Action<T> variable will not convert. The parameter
        // cannot be named `e`: OnStartup's own StartupEventArgs already owns that name in the
        // enclosing scope, and C# rejects the shadow (CS0136).
        ToastNotificationManagerCompat.OnActivated += activation =>
            OnToastActivated(activation.Argument);

        var acquiredMutex = SingleInstance.TryAcquire(out var instance);
        var action = StartupPlan.Decide(
            acquiredMutex,
            ToastNotificationManagerCompat.WasCurrentProcessToastActivated(),
            StartupPlan.WantsTestToasts(e.Args));

        if (action != LaunchAction.RunShell) instance?.Dispose();

        switch (action)
        {
            case LaunchAction.SendTestToastsAndExit:
                // Shuts itself down when the sends finish - it has real awaits in it.
                SendTestToastsThenExit();
                return;

            case LaunchAction.HandleToastActivationAndExit:
                // The handler above opens the link and shuts us down. This is the safety net for
                // an activation that never arrives - without it the process would linger with no
                // window and no tray icon.
                _exitAfterActivation = true;
                ExitAfter(TimeSpan.FromSeconds(5));
                return;

            case LaunchAction.SignalExistingAndExit:
                // Already running: hand the click to the live instance instead of adding a
                // second tray icon.
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
        _startup = new StartupManager();

        // The Run value is an absolute path, so it goes stale the moment the exe moves - Debug to
        // Release, or to wherever it ends up installed. Same dangling-registration problem the
        // toast COM entry has, same answer: rewrite it while we know where we are.
        if (_startup.IsEnabled && !_startup.IsCurrent)
        {
            ShellLog.Append("[Startup] run value points elsewhere - re-registering this exe");
            _startup.SetEnabled(true);
        }
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

        // Background thread in, Dispatcher out: HandleSystemResumeAsync is Core, and Core runs on
        // the Dispatcher. It debounces 30 s and invalidates the monitor's timecodes internally,
        // so the shell just has to ask.
        // The parameter cannot be named `_` - that would make the inner `_ =` discard an
        // assignment to this lambda's own string parameter instead (CS0029).
        _systemEvents = new SystemEventsWatcher(reason =>
            Dispatcher.InvokeAsync(() => _ = _orchestrator.HandleSystemResumeAsync()));
    }

    /// <summary>
    /// Republishes the <c>OnDeck.*</c> brushes for the current app theme. Every window binds
    /// them with <c>DynamicResource</c>, so a live theme change repaints in place. The acrylic
    /// tint is not a brush, so the two acrylic windows re-apply it explicitly.
    /// </summary>
    private void ApplyPalette()
    {
        ThemePalette.For(_theme!.AppsUseLightTheme).ApplyTo(Resources);
        _flyout?.RefreshBackdrop();
        _panel?.RefreshBackdrop();
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
            _settingsWindow = new SettingsWindow(_orchestrator!, _settingsStore!, _startup!);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    /// <summary>
    /// A toast was clicked. Fires on a background thread, so everything here hops to the
    /// Dispatcher — the context <c>AppOrchestrator</c> was constructed on.
    /// </summary>
    private void OnToastActivated(string argument)
    {
        var url = ToastActivation.UrlFrom(argument);
        ShellLog.Append(
            $"[Toast] activated argument=\"{argument}\" url={url?.AbsoluteUri ?? "(none)"}");

        Dispatcher.Invoke(() =>
        {
            if (url is not null) ExternalLink.Open(url);

            // This process exists only to service the click.
            if (_exitAfterActivation) Shutdown();
        });
    }

    /// <summary>
    /// <c>--test-toast</c>: one of each type, so the look, the headshot, the click-through and the
    /// Action Center behaviour can be checked without waiting for a live at-bat. Toggles are
    /// respected — whether a checkbox actually silences its type is one of the things to check.
    /// </summary>
    private void SendTestToastsThenExit()
    {
        // Off the Dispatcher deliberately. Core awaits without ConfigureAwait(false) — the
        // single-logical-thread rule — so blocking this thread on the prefetch would post the
        // continuation to the very thread doing the waiting and hang the process. Inside
        // Task.Run there is no SynchronizationContext, so the continuations land on the pool.
        _ = Task.Run(SendTestToastsAsync)
            .ContinueWith(_ => Dispatcher.InvokeAsync(Shutdown));
    }

    private static async Task SendTestToastsAsync()
    {
        var settings = new SettingsStore();

        // Bounded: this process has no UI, so a hung request would leave it invisible and alive.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var headshots = new HeadshotCache(http, HeadshotCache.DefaultCacheDirectory());
        var service = new ToastService(settings, headshots, new WindowsToastPresenter());

        const int playerId = 605141;     // Mookie Betts
        const int pitcherId = 657277;    // Logan Webb
        const int benchedId = 518692;    // Freddie Freeman
        const int gamePk = 776543;
        var stream = new Uri("https://www.mlb.com/tv/g776543");

        // These three are almost certainly not on the user's roster, so nothing has cached their
        // headshots. Without this the test toasts come up imageless and read as "headshots are
        // broken" - the opposite of what this switch is for. It also exercises the download path,
        // which is where the JPEG-vs-PNG bug lived.
        await headshots.PrefetchAsync([playerId, pitcherId, benchedId]);

        await service.NotifyBattingAsync(
            "Mookie Betts", playerId, gamePk, "SF 1 - LAD 2", "Bot 3", stream);
        await service.NotifyPitchingAsync(
            "Logan Webb", pitcherId, gamePk, "SF 1 - LAD 2", "Top 4", stream);
        await service.NotifyAtBatResultAsync(
            "Mookie Betts", playerId, "Home run to left field", stream);
        await service.NotifyPitchingResultAsync(
            "Logan Webb", pitcherId, "Logan Webb has been pulled from the game", stream);
        await service.NotifyNotInLineupAsync(
            "Freddie Freeman", benchedId, gamePk, "SF @ LAD",
            new Uri("https://www.fantrax.com/fantasy/league/lg1/home"));

        ShellLog.Append("[Toast] sent the --test-toast set");
    }

    private void ExitAfter(TimeSpan delay)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Shutdown();
        };
        timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _panel?.Close();
        _settingsWindow?.Close();
        _systemEvents?.Dispose();
        _tray?.Dispose();
        _theme?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
