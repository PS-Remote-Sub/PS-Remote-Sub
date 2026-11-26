using Avalonia.Controls;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.App.Views;
using PSRemoteSub.App.Views.Pages;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.App;

/// <summary>
/// Opens the region picker and the settings window when <see cref="AppHost"/> asks for them. UI thread only
/// (AppHost raises both events on the UI thread); every handler catches all exceptions.
/// </summary>
internal sealed class AppWindows : IDisposable
{
    private readonly AppHost _host;
    private SettingsWindow? _settings;
    private MainWindow? _main;
    private bool _picking;
    private bool _disposed;

    public AppWindows(AppHost host)
    {
        _host = host;
        _host.RegionSelectionRequested += OnRegionSelectionRequested;
        _host.MainWindowRequested += OnMainWindowRequested;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _host.RegionSelectionRequested -= OnRegionSelectionRequested;
        _host.MainWindowRequested -= OnMainWindowRequested;
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
            _settings?.Close();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        _settings = null;
        try
        {
            _main?.CloseForExit();
        }
        catch (Exception ex)
        {
            Log.Warn("Closing windows failed", ex); // exiting anyway
        }
        _main = null;
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
            var window = new MainWindow(_host, ShowLegacySettings);
            window.HomeFactory = () => new HomePage(_host, ShowLegacySettings);
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

    /// <summary>
    /// The settings page for a nav item, or null while it still lives in the old settings window (each Phase 2 page task
    /// adds its case; Task 10 removes the fallback).
    /// </summary>
    private SettingsPage? CreateSettingsPage(MainPage page)
    {
        switch (page)
        {
            case MainPage.Games: return new GamesPage(_host);
            case MainPage.Display: return new DisplayPage(_host);
            default: return null;
        }
    }

    /// <summary>Opens (or fronts) the old settings window on the tab holding a settings page. Phase 2 retires it.</summary>
    public void ShowLegacySettings(MainPage page)
    {
        try
        {
            ShowSettings();
            _settings?.SelectSection(page);
        }
        catch (Exception ex)
        {
            Log.Warn("Opening settings failed", ex); // must not take the app down
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
                _host.Notify(Loc.Get("NoFrameYet"));
                return;
            }

            suppressed = true;
            _host.SetOverlaySuppressed(true);
            // Saved to the profile the boxes were shown from, even if the active one changes while the editor is open.
            var profile = _host.Settings.GetActive();
            var start = profile.GetZone(zoneId) ?? profile.GetLastZone();
            var zones = profile.Zones
                .Select(z => new ZoneBoxes(z.Id, z.Name, z.Region, z.CaptionRegion, z.TriggerRegion,
                    ShowCaption: z.Placement == OverlayPlacement.Custom, ShowTrigger: z.TriggerEnabled))
                .ToList();
            var result = await RegionPickerWindow.PickAsync(frame, window.ClientScreenRect, zones, start.Id);
            if (result is not { } picked || _disposed)
                return;

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
            _host.Notify(Loc.Format("RegionSaved", profile.Name));
        }
        catch (Exception ex)
        {
            // The previous region stays.
            Log.Warn("Region pick failed", ex);
            if (!_disposed)
                _host.Notify(Loc.Get("RegionPickFailed"));
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
    private void ShowSettings()
    {
        if (_disposed)
            return;
        if (_settings is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var window = new SettingsWindow(_host);
        window.Closed += (_, _) =>
        {
            if (_settings == window)
                _settings = null;
        };
        _settings = window;
        window.Show();
        window.Activate();
    }
}
