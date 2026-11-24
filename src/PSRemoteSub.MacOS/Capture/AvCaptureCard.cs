using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// The capture card on macOS (AVFoundation, CaptureCard.swift): lists devices and modes, opens one in the mode
/// <see cref="CaptureModeChooser"/> picks, keeps its newest frame for the OCR pipeline (<see cref="Frames"/>), presents
/// its picture into <see cref="AvVideoSurface"/>s and plays its audio (<see cref="AvCaptureAudio"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Open"/> returns at once: the device's modes are listed and a native session opened on a pool thread. An
/// unknown device is <see cref="CaptureState.Unplugged"/>. The native session reports what happens
/// (<see cref="AvEvent"/>, mapped by <see cref="AvStateMapping"/>): Playing once frames arrive, NoSignal after 3 s
/// without one, InUse while another app holds the device. When it is unplugged, fails, is in use when opened or is not
/// authorized (<see cref="AvStateMapping.Reopens"/>), the session is closed and the device opened again every 3 s (Unplugged stays until it is back on the same USB port; the capture
/// window looks for it on another port). Camera access is never asked for here: without it the state is
/// <see cref="CaptureState.Blocked"/>.
/// </para>
/// <para>
/// Every state change and every native call happen under one lock; <see cref="StateChanged"/> is raised under it, in
/// order, on the thread that changed the state (a pool thread, the native session's queue, or the caller of
/// <see cref="Open"/> / <see cref="Close"/>): a handler must not block. <see cref="Dispose"/> stops presenting into any
/// surface still alive (its preview layer is detached; the view goes when the surface is disposed).
/// </para>
/// </remarks>
internal sealed class AvCaptureCard : ICaptureCard
{
    /// <summary>How <see cref="TakeNewerFrame"/> went.</summary>
    internal enum FrameRead { NotOpen, None, Newer }

    private sealed record VideoDto(string Id, string? Name, string? ModelId, bool BuiltIn);

    private sealed record AudioDto(string Id, string? Name, string? ModelId);

    private sealed record DevicesDto(List<VideoDto>? Video, List<AudioDto>? Audio);

    private sealed record ModeDto(int W, int H, double Fps, uint Fourcc);

    // psrs_av_copy_latest results.
    private const int CopyOk = 0, CopySizeOnly = 6;

    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly ConcurrentDictionary<int, AvCaptureCard> s_live = new();
    private static int s_nextId;

    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly Lock _gate = new();
    private readonly HashSet<AvVideoSurface> _surfaces = [];  // under _gate
    private readonly AvFrameSource _frames;
    private readonly Timer _retry;

    // All under _gate.
    private CaptureState _state = CaptureState.NoDevice;
    private string? _deviceId;              // the device opened, or null when closed
    private CaptureModeSetting? _wanted;
    private int _openId;                    // +1 per Open and Close: a late attempt or retry for an earlier one is dropped
    private nint _handle;                   // the native session, or 0
    private int _nativeId;                  // +1 per native session opened or closed: its late reports are dropped
    private CaptureMode? _currentMode;
    private IReadOnlyList<CaptureMode> _offeredModes = [];
    private long _lastFrameTicks;
    private string? _loggedOpen;
    private bool _disposed;

    public AvCaptureCard()
    {
        _frames = new AvFrameSource(this);
        _retry = new Timer(_ => Safe(Retry), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        s_live[_id] = this;
    }

    public CaptureState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<CaptureState>? StateChanged;

    public CaptureMode? CurrentMode
    {
        get { lock (_gate) return _currentMode; }
    }

    public IReadOnlyList<CaptureMode> OfferedModes
    {
        get { lock (_gate) return _offeredModes; }
    }

    public DateTimeOffset? LastFrameAt
    {
        get
        {
            lock (_gate)
            {
                if (_handle != 0 && Peek(out _, out _, out long ticks))
                    _lastFrameTicks = ticks;
                return _lastFrameTicks == 0 ? null : new DateTimeOffset(_lastFrameTicks, TimeSpan.Zero);
            }
        }
    }

    public IFrameSource Frames => _frames;

    public void Open(string deviceId, CaptureModeSetting? wanted)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        int openId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _retry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            CloseNativeLocked();
            _deviceId = deviceId;
            _wanted = wanted;
            openId = ++_openId;
            SetStateLocked(CaptureState.Opening);
        }
        ThreadPool.QueueUserWorkItem(_ => Safe(() => Attempt(openId)));
    }

    /// <summary>Stops reading and releases the device. Safe to call at any time, any number of times (also after Dispose).</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (!_disposed)
                CloseLocked();
        }
    }

    /// <summary>Lists external and built-in cameras with their paired audio input. Opens no device, so needs no permission.</summary>
    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync() => Task.Run<IReadOnlyList<CaptureDevice>>(() =>
    {
        int rc = Native.psrs_av_devices(out nint json);
        if (rc != 0)
            throw new InvalidOperationException($"Listing the capture devices failed (code {rc}).");
        DevicesDto? devices = Read<DevicesDto?>(json);
        var audios = (devices?.Audio ?? []).Select(a => new AvDevicePairing.Audio(a.Id, a.Name ?? "", a.ModelId ?? "")).ToList();
        return (devices?.Video ?? [])
            .Select(v => new CaptureDevice(v.Id, v.Name ?? "",
                v.BuiltIn ? null : AvDevicePairing.PairAudio(new(v.Id, v.Name ?? "", v.ModelId ?? ""), audios), v.BuiltIn))
            .ToList();
    });

    /// <summary>The modes of a device, one per size, maximum rate and pixel format. Empty for an unknown device.</summary>
    public IReadOnlyList<CaptureMode> ListModes(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        int rc = Native.psrs_av_modes(deviceId, out nint json);
        if (rc == 2)
            return [];
        if (rc != 0)
            throw new InvalidOperationException($"Listing the capture modes failed (code {rc}).");
        return Read<List<ModeDto>>(json).Select(m => AvModeMapping.ToMode(m.W, m.H, m.Fps, m.Fourcc)).ToList();
    }

    public ICaptureAudio CreateAudio() => new AvCaptureAudio();

    /// <summary>A new view showing the open session (black while there is none). UI (main) thread only; the parent is unused.</summary>
    public IVideoSurface CreateSurface(nint parentHandle)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var surface = AvVideoSurface.Create(_handle, Forget);
            _surfaces.Add(surface);
            return surface;
        }
    }

    /// <summary>
    /// The picture inside <paramref name="surface"/> in Avalonia's screen space (Quartz global points), letterboxed to
    /// the frame; minimised when the surface's window is (the capture window, <paramref name="topLevel"/>). Any thread.
    /// </summary>
    public WindowInfo? MeasurePicture(nint topLevel, IVideoSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface is not AvVideoSurface view
            || !view.TryMeasure(out double x, out double y, out double width, out double height, out bool letterboxed, out bool minimized))
            return null;
        return new WindowInfo(topLevel, Environment.ProcessId, PictureRect(x, y, width, height, letterboxed, CurrentMode), minimized);
    }

    /// <summary>
    /// The picture rect in screen pixels from a view rect in Quartz points (<see cref="MacCoordinates.ToScreenPixels"/>):
    /// as it is when the view measured the letterboxed picture itself, else fitted to <paramref name="mode"/> (the whole
    /// view without one).
    /// </summary>
    internal static PixelRect PictureRect(double x, double y, double width, double height, bool letterboxed, CaptureMode? mode)
    {
        var rect = MacCoordinates.ToScreenPixels(x, y, width, height);
        return letterboxed || mode is null ? rect : Letterbox.Fit(rect, mode.Width, mode.Height);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            CloseLocked();
            // Nothing may present into a surface the app disposes later (its view goes then).
            foreach (var surface in _surfaces)
                surface.Attach(0);
            _surfaces.Clear();
            _disposed = true;
        }
        _retry.Dispose();
        _frames.Dispose();
        s_live.TryRemove(_id, out _);
    }

    /// <summary>
    /// Copies the newest frame when it is newer than <paramref name="knownTicks"/>. <see cref="FrameRead.NotOpen"/> when
    /// no session is open, <see cref="FrameRead.None"/> when there is nothing newer.
    /// </summary>
    internal unsafe FrameRead TakeNewerFrame(long knownTicks, out Frame? frame, out long ticks)
    {
        frame = null;
        ticks = 0;
        lock (_gate)
        {
            if (_handle == 0)
                return FrameRead.NotOpen;
            if (!Peek(out int width, out int height, out ticks))
                return FrameRead.None;
            _lastFrameTicks = ticks;
            if (ticks == knownTicks)
                return FrameRead.None;
            var pixels = new byte[checked(width * height * 4)];
            int rc;
            int w, h;
            long copiedTicks;
            fixed (byte* p = pixels)
                rc = Native.psrs_av_copy_latest(_handle, p, pixels.Length, out w, out h, out copiedTicks);
            if (rc != CopyOk || w != width || h != height)
                return FrameRead.None; // a frame of another size arrived in between: the next read takes it
            ticks = copiedTicks;
            frame = new Frame(width, height, pixels);
            return FrameRead.Newer;
        }
    }

    // ---- Opening ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the device of <paramref name="openId"/> (pool or retry thread): lists its modes (none: unplugged), chooses
    /// one and opens a native session in it. Unplugged, a failure or no usable mode is tried again in 3 s.
    /// </summary>
    private unsafe void Attempt(int openId)
    {
        string deviceId;
        lock (_gate)
        {
            if (_disposed || openId != _openId || _deviceId is null)
                return;
            deviceId = _deviceId;
        }

        IReadOnlyList<CaptureMode>? modes;
        try
        {
            modes = ListModes(deviceId);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture card modes could not be listed", ex);
            modes = null;
        }

        lock (_gate)
        {
            if (_disposed || openId != _openId)
                return;
            CloseNativeLocked();
            if (modes is null || modes.Count == 0)
            {
                // Gone (or never there): stays so until it is back.
                SetStateLocked(modes is null ? CaptureState.Failed : CaptureState.Unplugged);
                ScheduleRetryLocked();
                return;
            }

            _offeredModes = modes;
            if (CaptureModeChooser.Choose(modes, _wanted) is not { } mode)
            {
                LogOpenOnce(deviceId, "Capture card offers no usable video mode");
                SetStateLocked(CaptureState.Failed);
                ScheduleRetryLocked();
                return;
            }
            _currentMode = mode;
            LogOpenOnce(deviceId, $"Capture card reading {mode}");
            _handle = Native.psrs_av_open(deviceId, mode.Width, mode.Height, mode.Fps, SubtypeRank(mode.Subtype),
                _id, ++_nativeId, &OnNativeState);
            if (_handle == 0)
            {
                SetStateLocked(CaptureState.Failed);
                ScheduleRetryLocked();
                return;
            }
            foreach (var surface in _surfaces)
                surface.Attach(_handle);
        }
    }

    /// <summary>The 3 s retry: opens the current device again.</summary>
    private void Retry()
    {
        int openId;
        lock (_gate)
        {
            if (_disposed || _deviceId is null)
                return;
            openId = _openId;
        }
        Attempt(openId);
    }

    private void ScheduleRetryLocked()
    {
        if (!_disposed)
            _retry.Change(RetryEvery, Timeout.InfiniteTimeSpan);
    }

    private static int SubtypeRank(CaptureSubtype subtype) => subtype switch
    {
        CaptureSubtype.Nv12 => 0,
        CaptureSubtype.Yuy2 => 1,
        CaptureSubtype.Mjpg => 2,
        _ => 3,
    };

    /// <summary>Logs a line about opening once per device and outcome, not on every 3 s retry.</summary>
    private void LogOpenOnce(string deviceId, string message)
    {
        string key = deviceId + "\n" + message;
        if (key == _loggedOpen)
            return;
        _loggedOpen = key;
        Log.Info(message);
    }

    // ---- Native session -------------------------------------------------------------------------------------------

    private void OnState(int nativeId, AvEvent e)
    {
        lock (_gate)
        {
            if (_disposed || _handle == 0 || nativeId != _nativeId)
                return;
            var state = AvStateMapping.ToState(e);
            if (AvStateMapping.Reopens(e))
            {
                // The session reports nothing more: let go of the device and open it again in 3 s.
                CloseNativeLocked();
                ScheduleRetryLocked();
            }
            if (state != _state && e != AvEvent.Running)
                Log.Info($"Capture card: {e}");
            SetStateLocked(state);
        }
    }

    /// <summary>Closes the native session (surfaces go black; its late reports are dropped).</summary>
    private void CloseNativeLocked()
    {
        if (_handle == 0)
            return;
        foreach (var surface in _surfaces)
            surface.Attach(0);
        Native.psrs_av_close(_handle);
        _handle = 0;
        _nativeId++;
    }

    private void CloseLocked()
    {
        _retry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _openId++;
        _deviceId = null;
        _loggedOpen = null;
        CloseNativeLocked();
        _currentMode = null;
        SetStateLocked(CaptureState.NoDevice);
    }

    private void SetStateLocked(CaptureState state)
    {
        if (_state == state)
            return;
        _state = state;
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture card StateChanged handler failed", ex);
        }
    }

    /// <summary>The newest frame's size and arrival time; false when there is none. Caller holds <c>_gate</c>, with a session open.</summary>
    private unsafe bool Peek(out int width, out int height, out long ticks)
    {
        int rc = Native.psrs_av_copy_latest(_handle, null, 0, out width, out height, out ticks);
        return rc == CopySizeOnly && width > 0 && height > 0;
    }

    private void Forget(AvVideoSurface surface)
    {
        lock (_gate)
            _surfaces.Remove(surface);
    }

    // Called by native code on any thread. Must never throw.
    [UnmanagedCallersOnly]
    private static void OnNativeState(int owner, int nativeId, int e)
    {
        try
        {
            if (Enum.IsDefined(typeof(AvEvent), e) && s_live.TryGetValue(owner, out var card))
                card.OnState(nativeId, (AvEvent)e);
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Capture card callback failed: {ex.Message}"); } catch { /* nothing more to do */ }
        }
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture card open failed", ex);
        }
    }

    private static T Read<T>(nint json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(Marshal.PtrToStringUTF8(json) ?? "null", Json)!;
        }
        finally
        {
            Native.psrs_free(json);
        }
    }
}
