using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Hardcodet.Wpf.TaskbarNotification;
using OnDeck.Core;

namespace OnDeck.App.Tray;

/// <summary>
/// The tray presence: an icon that greens up when a player is active, a tooltip carrying the
/// same text the Mac menu bar title would, and a right-click menu: Open, Float, Settings,
/// Refresh, Quit.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly AppOrchestrator _orchestrator;
    private readonly ThemeWatcher _theme = new();
    private readonly TaskbarIcon _icon;
    private TrayIcon? _current;

    public TrayIconService(AppOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;

        _icon = new TaskbarIcon { Visibility = Visibility.Visible };
        _icon.TrayLeftMouseUp += (_, _) => OpenRequested?.Invoke();
        _icon.ContextMenu = BuildMenu();

        _theme.Changed += Refresh;
        _orchestrator.StateChanged += Refresh;

        Refresh();
    }

    public event Action? OpenRequested;

    public event Action? FloatRequested;

    public event Action? SettingsRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    public void Refresh()
    {
        var wanted = TrayIconVariant.Select(_theme.SystemUsesLightTheme, _orchestrator.HasActivePlayers);
        if (_current != wanted)
        {
            _current = wanted;
            _icon.IconSource = new BitmapImage(new Uri(TrayIconVariant.ResourcePath(wanted)));
        }

        var title = _orchestrator.MenuBarTitleText;
        _icon.ToolTipText = title.Length == 0 ? "onDeck" : title;
    }

    private ContextMenu BuildMenu()
    {
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => OpenRequested?.Invoke();

        var floatPanel = new MenuItem { Header = "Float" };
        floatPanel.Click += (_, _) => FloatRequested?.Invoke();

        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => RefreshRequested?.Invoke();

        var quit = new MenuItem { Header = "Quit" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(floatPanel);
        menu.Items.Add(settings);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        return menu;
    }

    public void Dispose()
    {
        _orchestrator.StateChanged -= Refresh;
        _theme.Changed -= Refresh;
        _theme.Dispose();
        _icon.Dispose();
    }
}
