using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Input;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>Tray icon and its Thai menu. UI thread only; refreshes on <see cref="AppHost.StateChanged"/>.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppHost _host;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _state;
    private readonly NativeMenuItem _pause;
    private readonly NativeMenuItem _selectRegion;
    private readonly NativeMenuItem _profiles;
    private readonly NativeMenuItem _overlay;
    private readonly NativeMenuItem _regionFrames;
    private readonly NativeMenuItem _captureWindow;
    private readonly NativeMenuItem _web;
    private string[] _profileNames = [];
    private string? _activeProfile;

    public TrayController(Application app, AppHost host, Func<Task> exit)
    {
        _host = host;
        // NativeMenu items are text only: the header and the state line are disabled items, hotkeys are part of the text.
        var header = new NativeMenuItem(Loc.Get("TrayMenuTitle")) { IsEnabled = false };
        _state = new NativeMenuItem { IsEnabled = false };
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) => Safe(() => _host.SetPaused(!_host.IsPaused));

        _profiles = new NativeMenuItem(Loc.Get("TrayMenuGames")) { Menu = new NativeMenu() };

        _selectRegion = new NativeMenuItem();
        _selectRegion.Click += (_, _) => Safe(() => _host.RequestRegionSelection(null));

        // Shown only with the capture card as the picture source (see Update).
        _captureWindow = new NativeMenuItem(Loc.Get("TrayMenuCapture"));
        _captureWindow.Click += (_, _) => Safe(_host.RequestCaptureWindow);

        _overlay = new NativeMenuItem(Loc.Get("TrayMenuOverlay")) { ToggleType = MenuItemToggleType.CheckBox };
        _overlay.Click += (_, _) => Safe(() => _host.SetOverlayEnabled(!_host.Settings.OverlayEnabled));

        _regionFrames = new NativeMenuItem(Loc.Get("TrayMenuFrames")) { ToggleType = MenuItemToggleType.CheckBox };
        _regionFrames.Click += (_, _) => Safe(() => _host.SetRegionFramesEnabled(!_host.Settings.ShowRegionFrames));

        _web = new NativeMenuItem(Loc.Get("TrayMenuWeb")) { ToggleType = MenuItemToggleType.CheckBox };
        _web.Click += async (_, _) =>
        {
            try
            {
                await _host.SetWebEnabledAsync(!_host.Settings.WebEnabled); // not IsWebRunning: a failed start must not stick
                _host.SaveSettings();
            }
            catch (Exception ex)
            {
                Log.Warn("Tray: web toggle failed", ex); // AppHost also reports failures with a notification
            }
        };

        var openHome = new NativeMenuItem(Loc.Get("TrayOpenHome"));
        openHome.Click += (_, _) => Safe(() => _host.RequestMainWindow(MainPage.Home));

        var settings = new NativeMenuItem(Loc.Get("TrayMenuSettings"));
        settings.Click += (_, _) => Safe(_host.RequestSettings);

        var quit = new NativeMenuItem(Loc.Get("TrayMenuExit"));
        quit.Click += async (_, _) =>
        {
            try { await exit(); }
            catch (Exception ex) { Log.Warn("Tray: exit failed", ex); /* exit is best effort */ }
        };

        using var iconStream = AssetLoader.Open(new Uri("avares://PSRemoteSub.App/Assets/app.ico"));
        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[]
                 {
                     header, _state, new NativeMenuItemSeparator(),
                     openHome, _pause, _selectRegion, _profiles, new NativeMenuItemSeparator(),
                     _overlay, _web, _regionFrames, _captureWindow, new NativeMenuItemSeparator(), settings, quit,
                 })
            menu.Items.Add(item);
        _icon = new TrayIcon { Icon = new WindowIcon(iconStream), Menu = menu, ToolTipText = host.StatusText }; // macOS rejects a null tooltip
        // Windows: a left click opens Home. macOS shows the menu on click, and the menu's first item opens Home.
        _icon.Clicked += (_, _) => Safe(() => _host.RequestMainWindow(MainPage.Home));
        TrayIcon.SetIcons(app, [_icon]);

        _host.StateChanged += OnStateChanged;
        Update();
    }

    public void Dispose()
    {
        _host.StateChanged -= OnStateChanged;
        _icon.IsVisible = false;
        _icon.Dispose();
    }

    private void OnStateChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var summary = HomeStatus.Summarize(_host.Status, _host.IsPaused, DateTimeOffset.Now);
        _state.Header = summary.State switch
        {
            HomeState.Paused => "● " + Loc.Get("StatePaused"),
            HomeState.NeedsFix => "● " + Loc.Get("StateNeedsFix"),
            _ => "● " + Loc.Format("TrayStateTranslatingGame", _host.Settings.GetActive().Name),
        };
        _pause.Header = WithHotkey(Loc.Get(_host.IsPaused ? "TrayMenuResume" : "TrayMenuPause"), HotkeyAction.TogglePause);
        _selectRegion.Header = WithHotkey(Loc.Get("TrayMenuRegion"), HotkeyAction.SelectRegion);
        _overlay.IsChecked = _host.Settings.OverlayEnabled;
        _regionFrames.IsChecked = _host.Settings.ShowRegionFrames;
        _web.IsChecked = _host.IsWebRunning;
        _captureWindow.IsVisible = _host.PictureSource == PictureSource.CaptureCard;
        _icon.ToolTipText = _host.StatusText;
        UpdateProfiles();
    }

    /// <summary>The item text with its hotkey after it (platform symbols on Mac), as the New-Tray mockup shows it.</summary>
    private string WithHotkey(string text, HotkeyAction action)
    {
        var key = HotkeyLabels.DisplaySetting(_host.Settings.Hotkeys.GetValueOrDefault(action, ""), _host.Platform.Kind);
        return string.IsNullOrEmpty(key) ? text : $"{text}    {key}";
    }

    private void UpdateProfiles()
    {
        var names = _host.Settings.Profiles.Select(p => p.Name).ToArray();
        var active = _host.Settings.GetActive().Name;
        if (names.SequenceEqual(_profileNames) && active == _activeProfile)
            return;
        _profileNames = names;
        _activeProfile = active;

        var menu = _profiles.Menu!;
        menu.Items.Clear();
        foreach (var name in names)
        {
            var item = new NativeMenuItem(name)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = name == active,
            };
            item.Click += (_, _) => Safe(() => _host.SelectProfile(name));
            menu.Items.Add(item);
        }
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log.Warn("Tray: menu action failed", ex); /* must not take the app down */ }
    }
}
