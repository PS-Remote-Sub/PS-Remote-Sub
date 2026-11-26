using Avalonia.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Notices;
using PSRemoteSub.App.Views;
using PSRemoteSub.App.Views.Pages;
using PSRemoteSub.App.Views.Setup;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>
/// Opens the region picker and the main window when <see cref="AppHost"/> asks for them. UI thread only
/// (AppHost raises both events on the UI thread); every handler catches all exceptions.
/// </summary>
internal sealed class AppWindows : IDisposable
{
    private readonly AppHost _host;
    private MainWindow? _main;
    private SetupWindow? _setup;
    private PermissionsWindow? _permissions;
    private bool _picking;
    private bool _disposed;

    public AppWindows(AppHost host)
    {
        _host = host;
        _host.RegionSelectionRequested += OnRegionSelectionRequested;
        _host.MainWindowRequested += OnMainWindowRequested;
        _host.PermissionsWindowRequested += OnPermissionsWindowRequested;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.RegionSelectionRequested -= OnRegionSelectionRequested;
        _host.MainWindowRequested -= OnMainWindowRequested;
        _host.PermissionsWindowRequested -= OnPermissionsWindowRequested;
        try
        {
            RegionPickerWindow.CancelOpen();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        try
        {
            _main?.CloseForExit();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        _main = null;
        try
        {
            _setup?.CloseForExit();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        _setup = null;
        try
        {
            _permissions?.CloseForExit();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        _permissions = null;
    }

    private void OnPermissionsWindowRequested(object? sender, EventArgs e)
    {
        try
        {
            ShowPermissions();
        }
        catch (Exception ex)
        {
            Log.Warn("Opening the permissions window failed", ex);
        }
    }

    /// <summary>
    /// Shows the one macOS permissions window (created, or the open one fronted). <paramref name="whenClosed"/> runs when it
    /// closes by the user ("ทำทีหลัง", "เสร็จแล้ว" or X), not when the app exits; with the window already open it is added to
    /// the ones to run.
    /// </summary>
    public void ShowPermissions(Action? whenClosed = null)
    {
        if (_disposed)
            return;
        if (_permissions is null)
        {
            var window = new PermissionsWindow(_host);
            window.Closed += (_, _) =>
            {
                if (_permissions == window)
                    _permissions = null;
            };
            _permissions = window;
            window.Show();
        }
        var open = _permissions;
        if (whenClosed is not null)
        {
            open.Closed += (_, _) =>
            {
                if (_disposed || open.ClosedForExit)
                    return;
                try
                {
                    whenClosed();
                }
                catch (Exception ex)
                {
                    Log.Warn("Handling the end of the permissions window failed", ex);
                }
            };
        }
        if (open.WindowState == WindowState.Minimized)
            open.WindowState = WindowState.Normal;
        open.Activate();
    }

    /// <summary>
    /// Raised when the setup wizard ends (completed, skipped, or closed with X): see <see cref="SetupFinishedEventArgs"/>.
    /// Not raised when the app exits with the wizard open.
    /// </summary>
    public event EventHandler<SetupFinishedEventArgs>? SetupFinished;

    /// <summary>Shows the one setup wizard: creates it on its first step, or restores and fronts the open one. Hides nothing else.</summary>
    public void ShowSetup()
    {
        if (_disposed)
            return;
        if (_setup is null)
        {
            var window = new SetupWindow(_host);
            window.Finished += (_, e) =>
            {
                if (_setup == window)
                    _setup = null;
                try
                {
                    SetupFinished?.Invoke(this, e);
                }
                catch (Exception ex)
                {
                    Log.Warn("Handling the end of the setup wizard failed", ex);
                }
            };
            window.Closed += (_, _) =>
            {
                if (_setup == window)
                    _setup = null;
            };
            _setup = window;
            window.Show();
        }
        if (_setup.WindowState == WindowState.Minimized)
            _setup.WindowState = WindowState.Normal;
        _setup.Activate();
    }

    private void OnRegionSelectionRequested(object? sender, string? zoneId) => _ = PickRegionAsync(zoneId);

    private void OnMainWindowRequested(object? sender, MainPage page)
    {
        try
        {
            ShowMain(page);
        }
        catch (Exception ex)
        {
            Log.Warn("Opening the main window failed", ex); // must not take the app down
        }
    }

    /// <summary>Shows the one main window on a page: creates it, or shows/restores/fronts the hidden or open one.</summary>
    public void ShowMain(MainPage page)
    {
        if (_disposed)
            return;
        if (_main is null)
        {
            var window = new MainWindow(_host);
            window.HomeFactory = () => new HomePage(_host, window.ShowPage);
            window.PageFactory = CreateSettingsPage;
            window.Closed += (_, _) =>
            {
                if (_main == window)
                    _main = null;
            };
            _main = window;
        }
        if (!_main.IsVisible)
        {
            _main.Show();
            _main.ReloadCurrentPage(); // shown again on the same page: reload it from settings
        }
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.ShowPage(page);
        _main.Activate();
    }

    /// <summary>The settings page for a nav item (null for Home).</summary>
    private SettingsPage? CreateSettingsPage(MainPage page)
    {
        switch (page)
        {
            case MainPage.Games: return new GamesPage(_host);
            case MainPage.Display: return new DisplayPage(_host);
            case MainPage.Translation: return new TranslationPage(_host);
            case MainPage.Source: return new SourcePage(_host);
            case MainPage.Phone: return new PhonePage(_host);
            case MainPage.Tv: return new TvPage(_host);
            case MainPage.General: return new GeneralPage(_host, ShowSetup);
            default: return null;
        }
    }

    /// <summary>
    /// Never throws. Freezes the latest frame, lets the user edit the boxes of every zone of the active profile
    /// (starting on <paramref name="zoneId"/>, or the profile's last zone when null or gone), saves the edited zones and
    /// the last zone to that profile. The re-entrancy guard and the overlay suppression are always released, whatever
    /// fails.
    /// </summary>
    private async Task PickRegionAsync(string? zoneId)
    {
        if (_picking || _disposed)
            return;
        bool suppressed = false;
        try
        {
            _picking = true;
            var frame = _host.LatestFrame;
            if (frame is null || _host.TargetWindow is not { IsMinimized: false } window
                || window.ClientScreenRect.Width <= 0 || window.ClientScreenRect.Height <= 0)
            {
                _host.Notify(Notice.Done(Loc.Get("NoFrameYet"), topic: NoticeTopics.Region)); // a reply to the key
                return;
            }

            suppressed = true;
            _host.SetOverlaySuppressed(true);
            // Saved to the profile the boxes were shown from, even if the active one changes while the editor is open.
            var profile = _host.Settings.GetActive();
            var start = profile.GetZone(zoneId) ?? profile.GetLastZone();
            static ZoneBoxes ToBoxes(OcrZone z) => new(z.Id, z.Name, z.Region, z.CaptionRegion, z.TriggerRegion,
                ShowCaption: z.Placement == OverlayPlacement.Custom, ShowTrigger: z.TriggerEnabled);
            var zones = profile.Zones.Select(ToBoxes).ToList();
            // Zones added in the editor ("เพิ่มโซน"), made like the Games page makes them; they join the profile only
            // when the editor saves.
            var added = new List<OcrZone>();
            var result = await RegionPickerWindow.PickAsync(frame, window.ClientScreenRect, zones, start.Id, () =>
            {
                var zone = OcrZone.CreateNext(profile.Zones.Concat(added));
                added.Add(zone);
                return ToBoxes(zone);
            });
            if (result is not { } picked || _disposed)
                return;

            profile.Zones.AddRange(added);

            foreach (var edit in picked.Edits)
            {
                if (profile.GetZone(edit.Id) is not { } zone)
                    continue; // removed while the editor was open
                zone.Region = edit.Ocr;
                zone.CaptionRegion = edit.Caption;
                zone.TriggerRegion = edit.Trigger;
                // Moving the Thai box means "draw it there": a Fit zone would otherwise ignore it.
                if (edit.CaptionChanged)
                    zone.Placement = OverlayPlacement.Custom;
            }
            if (profile.GetZone(picked.LastZoneId) is not null)
                profile.LastZoneId = picked.LastZoneId;
            _host.ApplySettings();
            _host.SaveSettings();
            _host.Notify(Notice.Done(Loc.Get("RegionSaved"), Loc.Format("RegionSavedBody", profile.Name), NoticeTopics.Region));
        }
        catch (Exception ex)
        {
            // The previous region stays.
            Log.Warn("Region pick failed", ex);
            if (!_disposed)
                _host.Notify(Notice.Done(Loc.Get("RegionPickFailed"), Loc.Get("RegionPickFailedBody"), NoticeTopics.Region)); // a reply
        }
        finally
        {
            _picking = false;
            if (suppressed && !_disposed)
            {
                try
                {
                    _host.SetOverlaySuppressed(false);
                }
                catch (Exception ex)
                {
                    Log.Warn("Restoring the overlay after a pick failed", ex);
                }
            }
        }
    }
}
