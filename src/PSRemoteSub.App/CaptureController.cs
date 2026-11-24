using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>
/// Opens and closes the capture card window and drives what plays in it: the card (owned by <see cref="AppHost"/>),
/// which presents its picture into the window's surface, the card's audio (owned here), and the capture watcher's
/// probe, which reports where the picture is so the overlay can sit on it. Keeps <see cref="CaptureSettings"/> in step
/// with the toolbar and the window's placement.
/// </summary>
/// <remarks>
/// UI thread only (a call from another thread throws); otherwise no public member throws, failures are logged. Card
/// and audio events arrive on other threads and are posted to the UI
/// thread; one posted after a <see cref="Close"/> (or for an earlier opening) is dropped. While open, a missing card is
/// looked for every 3 s and the audio is started again when the picture comes back without it
/// (<see cref="CaptureWindowRules"/>). Teardown order on close:
/// card, audio, probe, then the window (and its surface, which stops presenting before its native view goes). Dispose
/// this before the card.
/// </remarks>
internal sealed class CaptureController : IDisposable
{
    private const double DefaultWidth = 1280;
    private const double DefaultHeight = 720;
    private const double DefaultMaxScreenShare = 0.9;
    private const int MinVisiblePixels = 64;
    private const int MaxPlacementPixels = 32768;
    private static readonly TimeSpan AudioGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VolumeSaveDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DeviceLookInterval = TimeSpan.FromSeconds(3);

    private readonly AppHost _host;
    private readonly PolledWindowWatcher _watcher;
    private readonly ICaptureCard _card;
    private readonly ICaptureAudio _audio;
    private readonly DispatcherTimer _audioCheck;
    private readonly DispatcherTimer _saveVolume;
    private readonly DispatcherTimer _deviceLook;  // while open: looks for a missing card again (CaptureWindowRules)

    private CaptureWindow? _window;
    // Read by the probe on the watcher's timer thread.
    private nint _topLevel;
    private IVideoSurface? _surface;
    // Bumped by every Open and Close: card and audio events posted for an earlier opening are dropped.
    private int _session;
    // Bumped by every device resolution and by Close: a device list that arrives late is dropped.
    private int _resolveGeneration;
    private string? _deviceId;               // the device the card was opened on, or null
    private CaptureModeSetting? _openedMode;  // the mode it was opened with
    private string? _audioDeviceId;          // the audio endpoint playing, or null
    private bool _audioStarting;             // started, not running yet, within its grace
    private int _resolvesInFlight;           // StartCaptureAsync calls still listing devices
    private CaptureState _handledState = CaptureState.NoDevice;  // the card state last shown
    private bool _refreshQueued;
    private bool _disposed;

    public CaptureController(AppHost host, PolledWindowWatcher watcher, ICaptureCard card)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(card);
        _host = host;
        _watcher = watcher;
        _card = card;
        _audio = card.CreateAudio();
        _audioCheck = new DispatcherTimer { Interval = AudioGrace };
        _audioCheck.Tick += (_, _) => Safe(OnAudioCheck);
        _saveVolume = new DispatcherTimer { Interval = VolumeSaveDelay };
        _saveVolume.Tick += (_, _) => Safe(() =>
        {
            _saveVolume.Stop();
            _host.SaveSettings();
        });
        _deviceLook = new DispatcherTimer { Interval = DeviceLookInterval };
        _deviceLook.Tick += (_, _) => Safe(OnDeviceLook);
        _audio.RunningChanged += OnAudioRunningChanged;
    }

    public bool IsOpen => _window is not null;

    private CaptureSettings Settings => _host.Settings.Capture;

    /// <summary>
    /// Shows the window (or brings it forward when open) at the saved placement, opens the device
    /// (<see cref="CaptureDeviceRules.ResolveDevice"/>) with the saved mode, and starts its audio.
    /// </summary>
    public void Open()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        if (_window is { } open)
        {
            Safe(open.Activate);
            return;
        }

        try
        {
            var window = new CaptureWindow(_card.CreateSurface);
            _window = window;
            _session++;

            double scaling = Place(window, Settings.Window, out var size);
            window.SetAudio(Settings.Volume, Settings.Muted);
            window.SetModes(CaptureModeChooser.Choices(_card.OfferedModes), Settings.Mode);
            window.ShowState(CaptureState.Opening);

            window.Closing += OnWindowClosing;
            window.PositionChanged += OnWindowMoved;
            window.Resized += OnWindowResized;
            window.ScalingChanged += OnWindowScalingChanged;
            window.PropertyChanged += OnWindowPropertyChanged;
            window.Host.PropertyChanged += OnHostPropertyChanged;
            window.Host.SurfaceChanged += OnSurfaceChanged;
            window.VolumeChanged += OnVolumeChanged;
            window.MutedChanged += OnMutedChanged;
            window.ModeChosen += OnModeChosen;

            window.Show();
            // Created at the primary screen's scaling: size it again for the screen it landed on.
            if (Math.Abs(window.RenderScaling - scaling) > 0.001)
            {
                window.Width = size.Width / window.RenderScaling;
                window.Height = size.Height / window.RenderScaling;
            }
            // After showing, so that leaving fullscreen returns to the placement.
            if (Settings.FullScreen)
                window.WindowState = WindowState.FullScreen;

            Volatile.Write(ref _topLevel, window.TryGetPlatformHandle()?.Handle ?? 0);
            _handledState = _card.State;
            _card.StateChanged += OnCardStateChanged;
            FollowSurface();
            _watcher.SetProbe(Probe);
            _host.SetCaptureWindowMinimized(window.WindowState == WindowState.Minimized);
            _host.SetCaptureWindowOpen(true);
            _ = StartCaptureAsync();
            _deviceLook.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("Opening the capture window failed", ex);
            Close();
        }
    }

    /// <summary>
    /// Saves the placement, stops the video, the card and its audio, detaches the probe (the overlay hides) and closes the
    /// window. Does nothing when closed.
    /// </summary>
    public void Close() => CloseCore(closeWindow: true);

    /// <summary>
    /// Applies <see cref="AppSettings.Capture"/> to an open window: audio, the mode list, and the device and mode (the card
    /// is reopened only when either changed). Does nothing when closed; <see cref="Open"/> reads the settings.
    /// </summary>
    public void ApplySettings()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_window is not { } window)
            return;
        Safe(() =>
        {
            window.SetAudio(Settings.Volume, Settings.Muted);
            _audio.Volume = Settings.Volume;
            _audio.Muted = Settings.Muted;
            window.SetModes(CaptureModeChooser.Choices(_card.OfferedModes), Settings.Mode);
            _ = StartCaptureAsync();
        });
    }

    /// <summary>
    /// After Camera or Microphone was granted (<see cref="AppHost.CapturePermissionsGranted"/>): an open window that started
    /// before the grant reopens the card (Camera) and starts its sound again (Microphone); a state change alone would not
    /// restart audio that never ran. Does nothing when closed.
    /// </summary>
    public void RetryAfterPermissionsGranted(IReadOnlyList<PlatformPermission> granted)
    {
        Dispatcher.UIThread.VerifyAccess();
        ArgumentNullException.ThrowIfNull(granted);
        if (_window is null)
            return;
        Safe(() =>
        {
            Log.Info($"Capture: restarting after the permission grant ({string.Join(", ", granted)})");
            if (granted.Contains(PlatformPermission.Camera))
            {
                _deviceId = null; // reopen even the same device and mode: it was blocked
                _openedMode = null;
            }
            if (granted.Contains(PlatformPermission.Microphone))
                StopAudio(); // StartCaptureAsync starts it again
            _ = StartCaptureAsync();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Close();
        _disposed = true;
        _audioCheck.Stop();
        _saveVolume.Stop();
        _deviceLook.Stop();
        _audio.RunningChanged -= OnAudioRunningChanged;
        Safe(_audio.Dispose);
    }

    // ---- Opening ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Positions and sizes <paramref name="window"/> before it is shown: the saved placement when it is usable, otherwise
    /// 1280×720 (shrunk to fit) centred on the primary screen. Returns the scaling the size was computed for, with the
    /// client size in physical pixels.
    /// </summary>
    private static double Place(CaptureWindow window, WindowPlacement? saved, out Size physicalSize)
    {
        var screens = window.Screens;
        if (saved is { Width: > 0 and <= MaxPlacementPixels, Height: > 0 and <= MaxPlacementPixels }
            && Math.Abs((long)saved.X) <= MaxPlacementPixels * 4L && Math.Abs((long)saved.Y) <= MaxPlacementPixels * 4L)
        {
            var rect = new PixelRect(saved.X, saved.Y, saved.Width, saved.Height);
            bool visible = screens.All.Any(s => s.WorkingArea.Intersect(rect) is
                { Width: >= MinVisiblePixels, Height: >= MinVisiblePixels });
            if (visible)
            {
                double savedScaling = (screens.ScreenFromBounds(rect) ?? screens.Primary)?.Scaling ?? 1;
                window.Position = new PixelPoint(saved.X, saved.Y);
                window.Width = saved.Width / savedScaling;
                window.Height = saved.Height / savedScaling;
                physicalSize = new Size(saved.Width, saved.Height);
                return savedScaling;
            }
        }

        // Missing, empty or on a monitor that is gone.
        var primary = screens.Primary ?? screens.All.FirstOrDefault();
        if (primary is null)
        {
            window.Width = DefaultWidth;
            window.Height = DefaultHeight;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            physicalSize = new Size(DefaultWidth, DefaultHeight);
            return 1;
        }
        double scaling = primary.Scaling;
        var area = primary.WorkingArea;
        double fit = Math.Min(1, Math.Min(
            area.Width * DefaultMaxScreenShare / (DefaultWidth * scaling),
            area.Height * DefaultMaxScreenShare / (DefaultHeight * scaling)));
        double width = DefaultWidth * fit;
        double height = DefaultHeight * fit;
        int pixelWidth = (int)Math.Round(width * scaling);
        int pixelHeight = (int)Math.Round(height * scaling);
        window.Width = width;
        window.Height = height;
        window.Position = new PixelPoint(area.X + (area.Width - pixelWidth) / 2, area.Y + (area.Height - pixelHeight) / 2);
        physicalSize = new Size(pixelWidth, pixelHeight);
        return scaling;
    }

    /// <summary>
    /// Resolves the device and opens it (unless it is already open with the wanted mode), then its audio (looked up again
    /// each time: its id can change). With no device found, an unplugged card keeps waiting for its return; otherwise the
    /// card closes and shows as missing. A newer call or a close while the device list loads wins. Never throws.
    /// </summary>
    private async Task StartCaptureAsync()
    {
        int generation = ++_resolveGeneration;
        _resolvesInFlight++;
        try
        {
            var devices = await _card.ListDevicesAsync();
            if (generation != _resolveGeneration || _window is not { } window)
                return;

            var device = CaptureDeviceRules.ResolveDevice(devices, Settings.DeviceId);
            if (device is null && _card.State == CaptureState.Unplugged)
                return; // still gone: the card reopens it when it returns on the same port, the device look otherwise
            if (device is null)
            {
                _deviceId = null;
                _openedMode = null;
                _card.Close();
                StopAudio();
                window.ShowAudioMissing(false);
                window.ShowState(CaptureState.NoDevice);
                return;
            }

            var mode = Settings.Mode;
            // The same device and mode are left alone: a session that lost its device looks for it again by itself.
            if (device.Id != _deviceId || !Equals(mode, _openedMode))
            {
                _deviceId = device.Id;
                _openedMode = mode;
                _card.Open(device.Id, mode);
            }
            StartAudio(window, device.AudioDeviceId);
        }
        catch (Exception ex)
        {
            Log.Warn("Opening the capture card failed", ex);
            if (generation == _resolveGeneration && _window is { } window)
                Safe(() => window.ShowState(CaptureState.Failed));
        }
        finally
        {
            _resolvesInFlight--;
        }
    }

    /// <summary>
    /// Every few seconds while open: a card that is missing (none found, or unplugged and perhaps back on another USB port
    /// with a new id) is looked for again and opened when found.
    /// </summary>
    private void OnDeviceLook()
    {
        if (_window is null || _resolvesInFlight > 0 || !CaptureWindowRules.ShouldLookForDevice(_card.State))
            return;
        _ = StartCaptureAsync();
    }

    private void StartAudio(CaptureWindow window, string? audioDeviceId)
    {
        _audio.Volume = Settings.Volume;
        _audio.Muted = Settings.Muted;
        if (audioDeviceId is null)
        {
            StopAudio();
            window.ShowAudioMissing(true);
            return;
        }
        if (audioDeviceId == _audioDeviceId && (_audio.IsRunning || _audioStarting))
            return; // already playing this card, or about to
        _audioDeviceId = audioDeviceId;
        window.ShowAudioMissing(false);
        _audio.Start(audioDeviceId);
        _audioStarting = true;
        RestartAudioCheck();
    }

    private void StopAudio()
    {
        _audioDeviceId = null;
        _audioStarting = false;
        _audioCheck.Stop();
        _audio.Stop();
    }

    private void RestartAudioCheck()
    {
        _audioCheck.Stop();
        _audioCheck.Start();
    }

    /// <summary>The card's audio gives no reason for a failure: audio that is not playing 3 s after a start counts as missing.</summary>
    private void OnAudioCheck()
    {
        _audioCheck.Stop();
        _audioStarting = false;
        if (_window is { } window && _audioDeviceId is not null && !_audio.IsRunning)
            window.ShowAudioMissing(true);
    }

    // ---- Closing ----------------------------------------------------------------------------------------------

    private void CloseCore(bool closeWindow)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_window is not { } window)
            return;
        _window = null;
        _session++;
        _resolveGeneration++;
        _audioCheck.Stop();
        _saveVolume.Stop();
        _deviceLook.Stop();

        Safe(() =>
        {
            if (window.NormalBounds is { } bounds)
                Settings.Window = bounds;
            Settings.FullScreen = window.WindowState == WindowState.FullScreen;
            _host.SaveSettings();
        });

        window.Closing -= OnWindowClosing;
        window.PositionChanged -= OnWindowMoved;
        window.Resized -= OnWindowResized;
        window.ScalingChanged -= OnWindowScalingChanged;
        window.PropertyChanged -= OnWindowPropertyChanged;
        window.Host.PropertyChanged -= OnHostPropertyChanged;
        window.Host.SurfaceChanged -= OnSurfaceChanged;
        window.VolumeChanged -= OnVolumeChanged;
        window.MutedChanged -= OnMutedChanged;
        window.ModeChosen -= OnModeChosen;
        _card.StateChanged -= OnCardStateChanged;

        Safe(_card.Close);
        _deviceId = null;
        _openedMode = null;
        Safe(StopAudio);
        Volatile.Write(ref _surface, null);
        Volatile.Write(ref _topLevel, 0);
        Safe(() => _watcher.SetProbe(static () => null));
        Safe(() => _host.SetCaptureWindowOpen(false));
        if (closeWindow)
            Safe(window.Close);
    }

    // ---- Window events (UI thread) -----------------------------------------------------------------------------

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (ReferenceEquals(sender, _window))
            CloseCore(closeWindow: false); // closing anyway: the surface goes with it
    }

    private void OnWindowMoved(object? sender, PixelPointEventArgs e) => QueueRefresh();

    private void OnWindowResized(object? sender, WindowResizedEventArgs e) => QueueRefresh();

    private void OnWindowScalingChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            QueueRefresh();
            if (sender is Window window)
                Safe(() => _host.SetCaptureWindowMinimized(window.WindowState == WindowState.Minimized));
        }
    }

    // The toolbar showing or hiding resizes the video without resizing the window.
    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
            QueueRefresh();
    }

    /// <summary>Raised inside Avalonia's native-control creation or destruction: must not throw.</summary>
    private void OnSurfaceChanged(object? sender, EventArgs e) => Safe(FollowSurface);

    /// <summary>Tracks the current surface for the probe (the card presents into it by itself).</summary>
    private void FollowSurface()
    {
        Volatile.Write(ref _surface, _window?.Host.Surface);
        QueueRefresh();
    }

    /// <summary>
    /// Refreshes the watcher once layout has settled (Avalonia moves the native surface after layout), coalescing a burst
    /// of moves into one probe.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued || _window is null)
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (_window is not null)
                Safe(_watcher.Refresh);
        }, DispatcherPriority.Background);
    }

    private void OnVolumeChanged(object? sender, double volume) => Safe(() =>
    {
        Settings.Volume = Math.Clamp(volume, 0, 1);
        _audio.Volume = Settings.Volume;
        // A drag sends many changes: save once it rests.
        _saveVolume.Stop();
        _saveVolume.Start();
    });

    private void OnMutedChanged(object? sender, bool muted) => Safe(() =>
    {
        Settings.Muted = muted;
        _audio.Muted = muted;
        _host.SaveSettings();
    });

    private void OnModeChosen(object? sender, CaptureModeSetting? mode) => Safe(() =>
    {
        if (Equals(mode, Settings.Mode))
            return;
        Settings.Mode = mode;
        _host.SaveSettings();
        if (_deviceId is { } deviceId)
        {
            _openedMode = mode;
            _card.Open(deviceId, mode);
        }
    });

    // ---- Card, audio and probe (any thread) --------------------------------------------------------------------

    private void OnCardStateChanged(object? sender, CaptureState state)
    {
        int session = Volatile.Read(ref _session);
        Dispatcher.UIThread.Post(() => Safe(() =>
        {
            if (session != _session || _window is not { } window)
                return;
            // The latest state, not the event's: the posts may lag the card (a reopen's NoDevice is usually stale).
            var current = _card.State;
            var previous = _handledState;
            _handledState = current;
            window.ShowState(current);
            window.SetModes(CaptureModeChooser.Choices(_card.OfferedModes), Settings.Mode);
            QueueRefresh(); // the frame size, and so the letterbox, may have changed
            // The picture is back (after an unplug or a sleep) but its sound is not: look the audio up again and start it.
            if (_resolvesInFlight == 0
                && CaptureWindowRules.ShouldRestartAudio(previous, current, _audio.IsRunning, _audioStarting))
                _ = StartCaptureAsync();
        }));
    }

    private void OnAudioRunningChanged(object? sender, bool running)
    {
        int session = Volatile.Read(ref _session);
        Dispatcher.UIThread.Post(() => Safe(() =>
        {
            if (session != _session || _window is not { } window || _audioDeviceId is null)
                return;
            if (running)
            {
                _audioCheck.Stop();
                _audioStarting = false;
                window.ShowAudioMissing(false);
            }
            else
            {
                RestartAudioCheck(); // dropped: missing unless it is back within 3 s
            }
        }));
    }

    /// <summary>
    /// The watcher's probe (its timer thread, or the UI thread on a refresh). Finds nothing unless the card plays: while it
    /// opens, has no signal or failed, the surface is hidden behind the state message and the overlay must not sit on it.
    /// A state change queues a refresh (<see cref="OnCardStateChanged"/>).
    /// </summary>
    private WindowInfo? Probe()
    {
        if (_card.State != CaptureState.Playing)
            return null;
        var surface = Volatile.Read(ref _surface);
        nint topLevel = Volatile.Read(ref _topLevel);
        if (surface is null || topLevel == 0)
            return null;
        return _card.MeasurePicture(topLevel, surface);
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture window controller failed", ex);
        }
    }
}
