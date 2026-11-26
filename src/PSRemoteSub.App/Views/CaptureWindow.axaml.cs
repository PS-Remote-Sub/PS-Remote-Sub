using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views;

/// <summary>
/// The capture card window: the video (a native surface in <see cref="Host"/>), a state message shown in its place when
/// there is no picture, and a toolbar (volume, mute, mode, fullscreen) that is hidden in fullscreen. F11 or a
/// double-click toggles fullscreen; Esc leaves it. UI thread only; it raises its events on the UI thread.
/// </summary>
/// <remarks>
/// It only shows and reports: <see cref="CaptureController"/> opens the card, applies the toolbar's changes and saves
/// them. A closed window cannot be shown again; the controller makes a new one for each opening.
/// </remarks>
internal sealed partial class CaptureWindow : Window
{
    private const double VolumeScale = 100;

    private readonly List<CaptureModeSetting?> _modes = [null];
    private readonly VideoHost _video;
    private IVideoSurface? _surface;         // the surface whose events are subscribed
    private WindowState _beforeFullScreen = WindowState.Normal;
    private bool _updating;                  // true while the controller sets the toolbar: raise nothing

    /// <param name="createSurface">Makes the video surface as a child of a native view (<see cref="ICaptureCard.CreateSurface"/>).</param>
    public CaptureWindow(Func<nint, IVideoSurface> createSurface)
    {
        InitializeComponent();
        // Built here rather than in XAML: Avalonia creates its native surface as soon as it joins the tree, so the
        // factory must be in place first.
        _video = new VideoHost(createSurface);
        ModeBox.ItemsSource = new[] { Loc.Get("CaptureModeAuto") };
        ModeBox.SelectedIndex = 0;

        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (!_updating)
                VolumeChanged?.Invoke(this, e.NewValue / VolumeScale);
        };
        MuteButton.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
                MutedChanged?.Invoke(this, MuteButton.IsChecked == true);
        };
        ModeBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && ModeBox.SelectedIndex is var i && i >= 0 && i < _modes.Count)
                ModeChosen?.Invoke(this, _modes[i]);
        };
        FullScreenButton.Click += (_, _) => ToggleFullScreen();
        RetryButton.Click += (_, _) => RetryRequested?.Invoke(this, EventArgs.Empty);
        PickDeviceButton.Click += (_, _) => PickDeviceRequested?.Invoke(this, EventArgs.Empty);
        VideoArea.DoubleTapped += (_, _) => ToggleFullScreen();
        // Tunnel, so a focused toolbar control cannot swallow F11 / Esc first.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        _video.SurfaceChanged += (_, _) => FollowSurface();
        VideoArea.Children.Insert(0, _video); // under the message
        FollowSurface();

        Opened += (_, _) => TrackNormalBounds();
        PositionChanged += (_, _) => PostTrackNormalBounds();
        Resized += (_, _) => PostTrackNormalBounds();
        ScalingChanged += (_, _) => PostTrackNormalBounds();
        Closed += (_, _) => FollowSurface(closing: true);
    }

    /// <summary>The volume slider moved: the new volume from 0 to 1.</summary>
    public event EventHandler<double>? VolumeChanged;

    /// <summary>The mute button was toggled: true when muted.</summary>
    public event EventHandler<bool>? MutedChanged;

    /// <summary>A mode was picked from the list: null for "อัตโนมัติ".</summary>
    public event EventHandler<CaptureModeSetting?>? ModeChosen;

    /// <summary>"ลองอีกครั้ง" was clicked in the no-picture panel.</summary>
    public event EventHandler? RetryRequested;

    /// <summary>"เลือกอุปกรณ์อื่น" was clicked in the no-picture panel.</summary>
    public event EventHandler? PickDeviceRequested;

    /// <summary>Hosts the video surface.</summary>
    public VideoHost Host => _video;

    /// <summary>
    /// The window's last bounds in the normal state (neither fullscreen, maximised nor minimised): <see cref="Window.Position"/>
    /// and the client size, in physical pixels. Null until the window has been shown.
    /// </summary>
    public WindowPlacement? NormalBounds { get; private set; }

    /// <summary>Shows the video when <paramref name="state"/> is <see cref="CaptureState.Playing"/>, otherwise its message in place of the video.</summary>
    public void ShowState(CaptureState state)
    {
        bool playing = state == CaptureState.Playing;
        bool opening = state == CaptureState.Opening;
        string text = playing ? "" : Loc.Get(CaptureText.MessageKey(state));
        Message.Text = text;
        Message.IsVisible = opening;
        // Anything else without a picture: the checklist, with the specific message under the heading.
        StateLine.Text = text;
        AutoRetry.IsVisible = CaptureWindowRules.ShouldLookForDevice(state); // the controller really is looking again
        NoVideo.IsVisible = !playing && !opening;
        // Airspace: the message cannot be drawn over the surface, so the surface makes way.
        _video.IsVisible = playing;
    }

    public void ShowAudioMissing(bool missing) => AudioMissing.IsVisible = missing;

    /// <summary>
    /// Fills the mode list: "อัตโนมัติ" (null) first, then <paramref name="choices"/>. Selects <paramref name="selected"/>, or
    /// "อัตโนมัติ" when it is null or not offered. Raises nothing.
    /// </summary>
    public void SetModes(IReadOnlyList<CaptureModeSetting> choices, CaptureModeSetting? selected)
    {
        int index = selected is null ? 0 : choices.ToList().IndexOf(selected) + 1;
        if (_modes.Skip(1).SequenceEqual(choices) && ModeBox.SelectedIndex == index)
            return; // unchanged: leave an open drop-down alone
        _updating = true;
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
            _updating = false;
        }
    }

    /// <summary>Sets the volume (0 to 1) and mute controls. Raises nothing.</summary>
    public void SetAudio(double volume, bool muted)
    {
        _updating = true;
        try
        {
            VolumeSlider.Value = Math.Clamp(volume, 0, 1) * VolumeScale;
            MuteButton.IsChecked = muted;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Enters borderless fullscreen, or leaves it for the state the window had before.</summary>
    public void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            LeaveFullScreen();
            return;
        }
        _beforeFullScreen = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = WindowState.FullScreen;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            Toolbar.IsVisible = WindowState != WindowState.FullScreen;
    }

    private void LeaveFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = _beforeFullScreen;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && WindowState == WindowState.FullScreen)
        {
            LeaveFullScreen();
            e.Handled = true;
        }
    }

    /// <summary>Moves the key and double-click subscriptions to the current surface (none once the window is closed).</summary>
    private void FollowSurface(bool closing = false)
    {
        var current = closing ? null : _video.Surface;
        if (ReferenceEquals(current, _surface))
            return;
        if (_surface is not null)
        {
            _surface.KeyPressed -= OnSurfaceKey;
            _surface.DoubleClicked -= OnSurfaceDoubleClicked;
        }
        _surface = current;
        if (_surface is not null)
        {
            _surface.KeyPressed += OnSurfaceKey;
            _surface.DoubleClicked += OnSurfaceDoubleClicked;
        }
    }

    // Raised inside the surface's own event handling (its window procedure on Windows): change the window state after.
    private void OnSurfaceKey(object? sender, VideoSurfaceKey key)
    {
        if (key == VideoSurfaceKey.ToggleFullScreen)
            Dispatcher.UIThread.Post(() => Safe(ToggleFullScreen));
        else if (key == VideoSurfaceKey.LeaveFullScreen)
            Dispatcher.UIThread.Post(() => Safe(LeaveFullScreen));
    }

    private void OnSurfaceDoubleClicked(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => Safe(ToggleFullScreen));

    // Moves and resizes are reported before the window state they belong to (a maximise arrives as a resize in the
    // normal state, then the state change): read the bounds once the state has settled.
    private void PostTrackNormalBounds() =>
        Dispatcher.UIThread.Post(() => Safe(TrackNormalBounds), DispatcherPriority.Background);

    private void TrackNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;
        double scaling = RenderScaling;
        var size = ClientSize;
        int width = (int)Math.Round(size.Width * scaling);
        int height = (int)Math.Round(size.Height * scaling);
        if (width > 0 && height > 0)
            NormalBounds = new WindowPlacement(Position.X, Position.Y, width, height);
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture window action failed", ex);
        }
    }
}
