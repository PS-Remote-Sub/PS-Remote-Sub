using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The "แหล่งภาพ" settings tab: Remote Play or the capture card, the card's device and mode, and the HDCP / HDR note.
/// The source goes through <see cref="AppHost.SetPictureSource"/>; a device or mode change is written to
/// <see cref="AppSettings.Capture"/> and applied with <see cref="AppHost.ApplyCaptureSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// UI thread only; every handler catches all exceptions. The device and mode lists load each time the tab is shown, not
/// when the panel is built (the settings window greets every launch). Device listing (WinRT, off the UI thread) opens no
/// device. A device is opened to list its modes only when the source is the capture card and the capture window is
/// closed: while the window is open the modes come from the engine that plays the card, and with Remote Play no device
/// is opened at all (it may be a laptop camera). A newer request, or <see cref="Detach"/>, drops an older one's result.
/// </para>
/// <para>When the card is unavailable on this PC (<see cref="AppHost.IsCaptureAvailable"/>), the card choice and lists
/// are disabled with a note.</para>
/// </remarks>
public partial class PictureSourcePanel : UserControl
{
    private readonly List<CaptureModeSetting?> _modes = [null];
    private AppHost? _host;
    private TabItem? _tab;  // the tab holding this panel: its lists load when it is selected
    private IReadOnlyList<CaptureDevice> _devices = [];
    private IReadOnlyList<CaptureMode>? _shownOffered;  // the offered modes the list was last built from
    private int _deviceSeq;
    private int _modeSeq;
    private bool _loading;  // true while the panel fills its controls: their change events are not the user's

    public PictureSourcePanel()
    {
        InitializeComponent();
        ModeBox.ItemsSource = new[] { Loc.Get("CaptureModeAuto") };
        ModeBox.SelectedIndex = 0;
        DeviceBox.IsEnabled = false;

        RemotePlayChoice.IsCheckedChanged += (_, _) => Safe(() => OnSourceChosen(RemotePlayChoice, PictureSource.RemotePlay));
        CaptureCardChoice.IsCheckedChanged += (_, _) => Safe(() => OnSourceChosen(CaptureCardChoice, PictureSource.CaptureCard));
        DeviceBox.SelectionChanged += (_, _) => Safe(OnDeviceChosen);
        ModeBox.SelectionChanged += (_, _) => Safe(OnModeChosen);
        RefreshButton.Click += (_, _) => _ = RefreshAsync();
    }

    /// <summary>
    /// Shows <paramref name="host"/>'s settings and follows its state until <see cref="Detach"/>. The device and mode
    /// lists load when the tab is shown (at once when the panel is not in a tab).
    /// </summary>
    public void Attach(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Detach();
        _host = host;
        host.StateChanged += OnStateChanged;
        ShowSource();

        bool available = host.IsCaptureAvailable;
        CaptureCardChoice.IsEnabled = available;
        UnavailableNote.IsVisible = !available;
        RefreshButton.IsEnabled = available;
        ModeBox.IsEnabled = available;
        if (!available)
            return; // nothing to list: the lists stay empty and disabled

        _tab = this.FindLogicalAncestorOfType<TabItem>();
        if (_tab is not null)
            _tab.PropertyChanged += OnTabPropertyChanged;
        if (_tab is null || _tab.IsSelected)
            _ = LoadDevicesAsync();
    }

    /// <summary>Stops following the host and the tab; device and mode lists still loading are dropped.</summary>
    public void Detach()
    {
        _deviceSeq++;
        _modeSeq++;
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab = null;
        }
        if (_host is null)
            return;
        _host.StateChanged -= OnStateChanged;
        _host = null;
    }

    private void OnTabPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TabItem.IsSelectedProperty && _tab is { IsSelected: true })
            _ = LoadDevicesAsync(); // shown: list again (a card may have been plugged in meanwhile)
    }

    private void OnStateChanged(object? sender, EventArgs e) => Safe(() =>
    {
        if (_host is not { } host)
            return;
        ShowSource();
        if (host.CaptureWindowOpen && host.CaptureCard is { } card)
        {
            // The card's list changes when it (re)opens a device; the window's toolbar may have picked another mode.
            var offered = card.OfferedModes;
            if (!ReferenceEquals(offered, _shownOffered) || !Equals(SelectedMode(), host.Settings.Capture.Mode))
            {
                _modeSeq++; // a listing still running is older than this
                ShowModes(offered);
            }
        }
    });

    private void ShowSource()
    {
        if (_host is not { } host)
            return;
        _loading = true;
        try
        {
            RemotePlayChoice.IsChecked = host.PictureSource == PictureSource.RemotePlay;
            CaptureCardChoice.IsChecked = host.PictureSource == PictureSource.CaptureCard;
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnSourceChosen(RadioButton button, PictureSource source)
    {
        if (_loading || _host is not { } host || button.IsChecked != true)
            return;
        host.SetPictureSource(source);
    }

    /// <summary>
    /// "รีเฟรช": lists the devices again and has an open capture window apply them, so a card plugged in meanwhile is
    /// opened even though the list already shows it selected. Never throws.
    /// </summary>
    private async Task RefreshAsync()
    {
        try
        {
            await LoadDevicesAsync();
            if (_host is { CaptureWindowOpen: true } host)
                host.ApplyCaptureSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Refreshing capture devices failed", ex);
        }
    }

    /// <summary>
    /// Lists the devices and selects the one the card opens (<see cref="CaptureDeviceRules.ResolveDevice"/>): none when
    /// the saved device is missing, so choosing any device then applies it. Never throws.
    /// </summary>
    private async Task LoadDevicesAsync()
    {
        try
        {
            int seq = ++_deviceSeq;
            if (_host is not { CaptureCard: { } card })
                return;
            IReadOnlyList<CaptureDevice> devices;
            try
            {
                devices = await card.ListDevicesAsync(); // enumeration only: opens no device
            }
            catch (Exception ex)
            {
                Log.Warn("Listing capture devices failed", ex);
                devices = [];
            }
            if (seq != _deviceSeq || _host is not { } host)
                return;

            _devices = devices;
            var selected = CaptureDeviceRules.ResolveDevice(devices, host.Settings.Capture.DeviceId);
            _loading = true;
            try
            {
                DeviceBox.ItemsSource = devices.Select(d => d.Name).ToList();
                DeviceBox.SelectedIndex = selected is null ? -1 : devices.ToList().IndexOf(selected);
                DeviceBox.IsEnabled = devices.Count > 0;
            }
            finally
            {
                _loading = false;
            }
            await LoadModesAsync(selected);
        }
        catch (Exception ex)
        {
            Log.Warn("Showing capture devices failed", ex);
        }
    }

    /// <summary>
    /// Fills the mode list for <paramref name="device"/>. Opens the device (on the thread pool) only when the source is the
    /// capture card and its window is closed; while the window is open the card's modes are used, and with Remote Play
    /// whatever the card last saw (no device is opened). Never throws.
    /// </summary>
    private async Task LoadModesAsync(CaptureDevice? device)
    {
        try
        {
            int seq = ++_modeSeq;
            if (_host is not { CaptureCard: { } card } host)
                return;
            IReadOnlyList<CaptureMode> offered;
            if (device is null)
                offered = [];
            else if (host.CaptureWindowOpen || host.PictureSource != PictureSource.CaptureCard)
                offered = card.OfferedModes;
            else
            {
                string id = device.Id;
                try
                {
                    offered = await Task.Run(() => card.ListModes(id));
                }
                catch (Exception ex)
                {
                    Log.Warn("Listing capture modes failed", ex);
                    offered = [];
                }
            }
            if (seq != _modeSeq || _host is null)
                return;
            ShowModes(offered);
        }
        catch (Exception ex)
        {
            Log.Warn("Showing capture modes failed", ex);
        }
    }

    /// <summary>
    /// "อัตโนมัติ" first, then the usable choices; selects the saved mode, or "อัตโนมัติ" when the device does not offer it.
    /// With no modes known (none listed yet), the saved mode is still shown as the choice it is.
    /// </summary>
    private void ShowModes(IReadOnlyList<CaptureMode> offered)
    {
        if (_host is not { } host)
            return;
        _shownOffered = offered;
        var wanted = host.Settings.Capture.Mode;
        var choices = CaptureModeChooser.Choices(offered);
        if (choices.Count == 0 && wanted is not null)
            choices = [wanted];
        int index = wanted is null ? 0 : choices.ToList().IndexOf(wanted) + 1;
        if (_modes.Skip(1).SequenceEqual(choices) && ModeBox.SelectedIndex == index)
            return; // unchanged: leave an open drop-down alone
        _loading = true;
        try
        {
            _modes.Clear();
            _modes.Add(null);
            _modes.AddRange(choices);
            ModeBox.ItemsSource = _modes.Select(CaptureText.ModeLabel).ToList();
            ModeBox.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    private CaptureModeSetting? SelectedMode() =>
        ModeBox.SelectedIndex is var i && i > 0 && i < _modes.Count ? _modes[i] : null;

    private void OnDeviceChosen()
    {
        if (_loading || _host is not { } host)
            return;
        int i = DeviceBox.SelectedIndex;
        if (i < 0 || i >= _devices.Count)
            return;
        var device = _devices[i];
        if (device.Id == host.Settings.Capture.DeviceId)
            return;
        host.Settings.Capture.DeviceId = device.Id;
        host.ApplyCaptureSettings(); // saves; an open window reopens on this device
        _ = LoadModesAsync(device);
    }

    private void OnModeChosen()
    {
        if (_loading || _host is not { } host)
            return;
        int i = ModeBox.SelectedIndex;
        if (i < 0 || i >= _modes.Count)
            return;
        var mode = _modes[i];
        if (Equals(mode, host.Settings.Capture.Mode))
            return;
        host.Settings.Capture.Mode = mode;
        host.ApplyCaptureSettings(); // saves; an open window reopens with this mode
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Picture source settings failed", ex);
        }
    }
}
