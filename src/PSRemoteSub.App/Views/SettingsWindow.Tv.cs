using System.Globalization;
using Avalonia.Threading;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The "ทีวี" tab: subtitles on the Android TV on or off, their look and calibration (pushed to the TV live, saved
/// debounced), and installing the TV app over ADB. The calibration pattern is turned off when the window closes.
/// </summary>
public partial class SettingsWindow
{
    private const string DefaultAdbPort = "5555";

    // This window started the running install, so its own step texts are showing.
    private bool _tvInstallRunHere;

    private void WireTvEvents()
    {
        TvEnabled.IsCheckedChanged += (_, _) => _ = SetTvEnabledAsync();
        TvModeCover.IsCheckedChanged += (_, _) => SelectTvMode();
        TvModeStrip.IsCheckedChanged += (_, _) => SelectTvMode();
        TvTextScale.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.TextScale = (int)Math.Round(TvTextScale.Value));
        };
        TvBoxOpacity.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.BoxOpacity = Math.Round(Math.Clamp(TvBoxOpacity.Value, 0, 1), 2));
        };
        TvStripBottom.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.StripBottom = (int)Math.Round(TvStripBottom.Value));
        };
        TvCalibScale.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibScale = Math.Round(TvCalibScale.Value, 4));
        };
        TvCalibX.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibOffsetX = Math.Round(TvCalibX.Value, 4));
        };
        TvCalibY.ValueChanged += (_, _) =>
        {
            Safe(ShowTvSliderTexts);
            EditTv(t => t.CalibOffsetY = Math.Round(TvCalibY.Value, 4));
        };
        TvCalibrating.IsCheckedChanged += (_, _) => Safe(() =>
        {
            if (!_loading && !_closed)
                Host.SetTvCalibrating(TvCalibrating.IsChecked == true);
        });
        TvCalibReset.Click += (_, _) => Safe(ResetTvCalibration);
        TvInstall.Click += (_, _) => _ = InstallTvAppAsync();
        TvLocalNetworkSettings.Click += (_, _) =>
            Safe(() => Host.OpenPermissionSettings(PlatformPermission.LocalNetwork));
    }

    /// <summary>Call with <see cref="_loading"/> set: fills the tab from the settings without writing back.</summary>
    private void LoadTv()
    {
        var tv = Settings.Tv;
        TvEnabled.IsChecked = tv.Enabled;
        TvModeCover.IsChecked = tv.Mode == TvMode.Cover;
        TvModeStrip.IsChecked = tv.Mode == TvMode.Strip;
        TvTextScale.Value = Math.Clamp(tv.TextScale, 50, 200);
        TvBoxOpacity.Value = Math.Clamp(tv.BoxOpacity, 0, 1);
        TvStripBottom.Value = Math.Clamp(tv.StripBottom, 0, 60);
        LoadTvCalibration();
        TvCalibrating.IsChecked = false; // closing the window turned it off
        TvAddress.Text = tv.AdbAddress ?? "";
        ShowTvSliderTexts();
        ShowTvStripRow();
    }

    /// <summary>Call with <see cref="_loading"/> set.</summary>
    private void LoadTvCalibration()
    {
        var tv = Settings.Tv;
        TvCalibScale.Value = double.IsNaN(tv.CalibScale) ? 1 : Math.Clamp(tv.CalibScale, 0.8, 1.2);
        TvCalibX.Value = double.IsNaN(tv.CalibOffsetX) ? 0 : Math.Clamp(tv.CalibOffsetX, -0.1, 0.1);
        TvCalibY.Value = double.IsNaN(tv.CalibOffsetY) ? 0 : Math.Clamp(tv.CalibOffsetY, -0.1, 0.1);
    }

    /// <summary>The on/off box (an install may have turned it on), the connected TVs and the install button.</summary>
    private void LoadTvState()
    {
        _loading = true;
        try
        {
            TvEnabled.IsChecked = Settings.Tv.Enabled;
        }
        finally
        {
            _loading = false;
        }
        var clients = Host.TvFeed.Clients().Distinct(StringComparer.Ordinal).ToList();
        bool connected = clients.Count > 0;
        TvState.Text = connected ? Loc.Format("TvConnected", string.Join(", ", clients)) : Loc.Get("TvNotConnected");
        TvState.Classes.Set("ok", connected);
        TvLocalNetworkHint.IsVisible = Host.TvLocalNetworkBlocked;
        bool busy = Host.IsInstallingTvApp;
        TvInstall.IsEnabled = !busy;
        // ADB work this window did not start (the startup update, or an install from before it was reopened).
        if (busy && !_tvInstallRunHere)
        {
            TvInstallStepText.Text = Loc.Get("TvInstallBusy");
            TvInstallStepText.IsVisible = true;
        }
        else if (!busy && !_tvInstallRunHere && TvInstallStepText.Text == Loc.Get("TvInstallBusy"))
        {
            TvInstallStepText.IsVisible = false;
        }
    }

    /// <summary>Feed thread: only who is connected matters here; redrawn on the UI thread. Never throws.</summary>
    private void OnTvFeedChanged(object? sender, TvFeedEvent e)
    {
        if (e is not TvClientsChanged)
            return;
        try
        {
            Dispatcher.UIThread.Post(() => Safe(() =>
            {
                if (!_closed)
                    LoadTvState();
            }));
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV state update failed: {ex.GetType().Name}"); // the dispatcher is shutting down
        }
    }

    private async Task SetTvEnabledAsync()
    {
        try
        {
            if (_loading || _closed)
                return;
            await Host.SetTvEnabledAsync(TvEnabled.IsChecked == true);
            Host.SaveSettings();
            if (!_closed)
                LoadTvState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV toggle failed: {ex.GetType().Name}"); // AppHost reports server start failures itself
        }
    }

    private void SelectTvMode() => EditTv(t =>
    {
        if (TvModeStrip.IsChecked == true)
            t.Mode = TvMode.Strip;
        else if (TvModeCover.IsChecked == true)
            t.Mode = TvMode.Cover;
    }, afterEdit: ShowTvStripRow);

    /// <summary>
    /// Applies a TV setting edit: pushed to the TV at once (no save per slider tick), saved debounced like the other
    /// settings.
    /// </summary>
    private void EditTv(Action<TvSettings> edit, Action? afterEdit = null) => Safe(() =>
    {
        if (_loading || _closed)
            return;
        edit(Settings.Tv);
        Host.PushTvConfig();
        ScheduleSave();
        afterEdit?.Invoke();
    });

    private void ResetTvCalibration()
    {
        if (_closed)
            return;
        var tv = Settings.Tv;
        tv.CalibScale = 1.0;
        tv.CalibOffsetX = 0;
        tv.CalibOffsetY = 0;
        _loading = true;
        try
        {
            LoadTvCalibration();
            ShowTvSliderTexts();
        }
        finally
        {
            _loading = false;
        }
        Host.PushTvConfig();
        ScheduleSave();
    }

    /// <summary>The distance from the bottom edge applies to the strip only.</summary>
    private void ShowTvStripRow()
    {
        bool strip = TvModeStrip.IsChecked == true;
        TvStripBottomLabel.IsVisible = strip;
        TvStripBottomRow.IsVisible = strip;
    }

    private void ShowTvSliderTexts()
    {
        var c = CultureInfo.InvariantCulture;
        TvTextScaleText.Text = Math.Round(TvTextScale.Value).ToString("0", c) + "%";
        TvBoxOpacityText.Text = FormatPercent(TvBoxOpacity.Value);
        TvStripBottomText.Text = Math.Round(TvStripBottom.Value).ToString("0", c) + "%";
        TvCalibScaleText.Text = (TvCalibScale.Value * 100).ToString("0.0", c) + "%";
        TvCalibXText.Text = (TvCalibX.Value * 100).ToString("+0.00;-0.00;0", c) + "%";
        TvCalibYText.Text = (TvCalibY.Value * 100).ToString("+0.00;-0.00;0", c) + "%";
    }

    /// <summary>
    /// The typed TV address as an adb endpoint: blank = find the TV on the LAN (null); a bare IP gets adb's default
    /// port. False when it is not a valid <c>host:port</c>.
    /// </summary>
    private static bool TryParseTvAddress(string? text, out string? endpoint)
    {
        endpoint = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return true;
        if (!trimmed.Contains(':', StringComparison.Ordinal))
            trimmed += ":" + DefaultAdbPort;
        if (!AdbClient.IsValidEndpoint(trimmed))
            return false;
        endpoint = trimmed;
        return true;
    }

    /// <summary>
    /// Installs the TV app through <see cref="AppHost.InstallTvAppAsync"/> (which saves the address and turns the TV on
    /// when it works), showing each step and the final error. The button stays disabled while it runs.
    /// </summary>
    private async Task InstallTvAppAsync()
    {
        try
        {
            if (_closed || Host.IsInstallingTvApp)
                return;
            TvInstallError.IsVisible = false;
            if (!TryParseTvAddress(TvAddress.Text, out var endpoint))
            {
                ShowTvInstallError("TvAddressInvalid");
                return;
            }
            if (endpoint is not null)
                TvAddress.Text = endpoint;

            TvInstall.IsEnabled = false;
            TvInstallStepText.IsVisible = false;
            // Progress<T> posts to the UI thread (captured here).
            var progress = new Progress<TvInstallStep>(step => Safe(() =>
            {
                if (_closed)
                    return;
                TvInstallStepText.Text = Loc.Get("TvInstallStep" + step);
                TvInstallStepText.IsVisible = true;
            }));
            TvInstallResult result;
            _tvInstallRunHere = true;
            try
            {
                result = await Host.InstallTvAppAsync(endpoint, progress);
            }
            finally
            {
                _tvInstallRunHere = false;
            }
            if (_closed)
                return;
            if (result.Ok)
            {
                TvInstallStepText.Text = Loc.Get("TvInstallDone");
                TvInstallStepText.IsVisible = true;
                _loading = true;
                try
                {
                    TvAddress.Text = Settings.Tv.AdbAddress ?? "";
                }
                finally
                {
                    _loading = false;
                }
            }
            else
            {
                TvInstallStepText.IsVisible = false;
                ShowTvInstallError(result.ErrorKey ?? "TvInstallFailed");
            }
            LoadTvState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings: TV install failed: {ex.GetType().Name}");
            if (!_closed)
            {
                ShowTvInstallError("TvInstallFailed");
                LoadTvState();
            }
        }
    }

    private void ShowTvInstallError(string key)
    {
        TvInstallError.Text = Loc.Get(key);
        TvInstallError.IsVisible = true;
    }
}
