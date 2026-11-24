using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>Tray icon and its Thai menu. UI thread only; refreshes on <see cref="AppHost.StateChanged"/>.</summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppHost _host;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _pause;
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
        _pause = new NativeMenuItem();
        _pause.Click += (_, _) => Safe(() => _host.SetPaused(!_host.IsPaused));

        _profiles = new NativeMenuItem(Loc.Get("TrayProfiles")) { Menu = new NativeMenu() };

        var selectRegion = new NativeMenuItem(Loc.Get("TraySelectRegion"));
        selectRegion.Click += (_, _) => Safe(() => _host.RequestRegionSelection(null));

        // Shown only with the capture card as the picture source (see Update).
        _captureWindow = new NativeMenuItem(Loc.Get("TrayCaptureWindow"));
        _captureWindow.Click += (_, _) => Safe(_host.RequestCaptureWindow);

        _overlay = new NativeMenuItem(Loc.Get("TrayOverlay")) { ToggleType = MenuItemToggleType.CheckBox };
        _overlay.Click += (_, _) => Safe(() => _host.SetOverlayEnabled(!_host.Settings.OverlayEnabled));

        _regionFrames = new NativeMenuItem(Loc.Get("TrayRegionFrames")) { ToggleType = MenuItemToggleType.CheckBox };
        _regionFrames.Click += (_, _) => Safe(() => _host.SetRegionFramesEnabled(!_host.Settings.ShowRegionFrames));

        _web = new NativeMenuItem(Loc.Get("TrayWeb")) { ToggleType = MenuItemToggleType.CheckBox };
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

        var settings = new NativeMenuItem(Loc.Get("TraySettings"));
        settings.Click += (_, _) => Safe(_host.RequestSettings);

        var quit = new NativeMenuItem(Loc.Get("TrayExit"));
        quit.Click += async (_, _) =>
        {
            try { await exit(); }
            catch (Exception ex) { Log.Warn("Tray: exit failed", ex); /* exit is best effort */ }
        };

        using var iconStream = AssetLoader.Open(new Uri("avares://PSRemoteSub.App/Assets/app.ico"));
        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[]
                 {
                     _pause, _profiles, selectRegion, _captureWindow, new NativeMenuItemSeparator(),
                     _overlay, _regionFrames, _web, new NativeMenuItemSeparator(), settings, quit,
                 })
            menu.Items.Add(item);
        _icon = new TrayIcon { Icon = new WindowIcon(iconStream), Menu = menu, ToolTipText = host.StatusText }; // macOS rejects a null tooltip
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
        _pause.Header = Loc.Get(_host.IsPaused ? "TrayResume" : "TrayPause");
        _overlay.IsChecked = _host.Settings.OverlayEnabled;
        _regionFrames.IsChecked = _host.Settings.ShowRegionFrames;
        _web.IsChecked = _host.IsWebRunning;
        _captureWindow.IsVisible = _host.PictureSource == PictureSource.CaptureCard;
        _icon.ToolTipText = _host.StatusText;
        UpdateProfiles();
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
