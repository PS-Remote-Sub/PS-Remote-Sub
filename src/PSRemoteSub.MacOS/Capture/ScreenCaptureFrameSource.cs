using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// Captures one window (the CGWindowID in <see cref="WindowInfo.Handle"/>) with ScreenCaptureKit and publishes its
/// client area as BGRA8 <see cref="Frame"/>s.
/// </summary>
/// <remarks>
/// The native side (ScreenCapture.swift) starts the stream asynchronously, so <see cref="Start"/> returns at once and
/// never prompts: without Screen Recording permission nothing is captured, <see cref="Latest"/> stays null and that is
/// logged once. Frames (whole window, backing pixels, at most 10 fps, no cursor, following window resizes) arrive on a
/// Swift queue that keeps only the newest one. As with the Windows capture, the copy into a managed <see cref="Frame"/>
/// happens lazily in <see cref="Latest"/>, at most once per arrived frame, and the same object is returned until a new
/// frame arrives. The title bar is cropped there: its height in pixels follows from the frame size and the current
/// client rect (<see cref="MacCoordinates.TopInsetRows"/>), taken from <c>currentWindow</c> (the watcher) when it
/// reports the same window, else from the <see cref="WindowInfo"/> given to <see cref="Start"/>, because the pipeline
/// restarts capture only when the window itself changes, not when it moves, resizes or goes full screen.
/// <para>
/// Because the native start is asynchronous, <see cref="Start"/> cannot report a failure the way the Windows capture
/// does (by throwing, which makes the pipeline retry). So a capture that later fails — the window not (yet) shareable,
/// or the stream failing or being stopped (sleep/wake, a display change, Stop in the menu bar) — is restarted here every
/// <see cref="RetryBackoff"/> while the window stays the same (<see cref="CaptureRetryPolicy"/>); the first failure
/// and the recovery are logged, not every retry. Missing permission is never retried.
/// </para>
/// </remarks>
public sealed class ScreenCaptureFrameSource(Func<WindowInfo?>? currentWindow = null) : IFrameSource
{
    // psrs_sck_copy_latest results (ScreenCapture.swift).
    private const int Ok = 0, NoFrame = 2, NoPermission = 3, WindowNotFound = 4, Failed = 5, SizeOnly = 6;

    /// <summary>How often a failed capture of the same window is started again.</summary>
    internal static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(3);

    private static int s_permissionLogged;

    private readonly Lock _gate = new();

    // All under _gate.
    private nint _handle;
    private WindowInfo? _window;
    private Frame? _latest;
    private long _latestTicks;
    private long _lastFrameTicks;
    private readonly CaptureRetryPolicy _retry = new(RetryBackoff);
    private byte[] _scratch = [];
    private bool _disposed;

    /// <summary>The newest client-area frame; copied from the native side only when a new frame has arrived. Thread-safe.</summary>
    public Frame? Latest
    {
        get
        {
            lock (_gate)
            {
                if (Peek(out int width, out int height, out long ticks) && ticks != _latestTicks)
                    TryCopy(width, height, ticks);
                return _latest;
            }
        }
    }

    public DateTimeOffset? LastFrameAt
    {
        get
        {
            lock (_gate)
            {
                Peek(out _, out _, out _);
                return _lastFrameTicks == 0 ? null : new DateTimeOffset(_lastFrameTicks, TimeSpan.Zero);
            }
        }
    }

    public void Start(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            _window = window;
            _latest = null;
            _latestTicks = 0;
            _lastFrameTicks = 0;
            _retry.Reset();
            _handle = Native.psrs_sck_start(unchecked((uint)window.Handle));
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopCore();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopCore();
            _disposed = true;
            _scratch = [];
        }
    }

    private void StopCore()
    {
        if (_handle == 0)
            return;
        Native.psrs_sck_stop(_handle);
        _handle = 0;
    }

    /// <summary>Asks the native side for the newest frame's size and arrival time. Caller holds <c>_gate</c>.</summary>
    private unsafe bool Peek(out int width, out int height, out long ticks)
    {
        width = height = 0;
        ticks = 0;
        if (_handle == 0)
            return false;
        int rc = Native.psrs_sck_copy_latest(_handle, null, 0, out width, out height, out ticks);
        Observe(rc);
        if (rc != SizeOnly)
            return false;
        _lastFrameTicks = ticks;
        return width > 0 && height > 0;
    }

    /// <summary>Copies the newest frame and crops the title bar. Caller holds <c>_gate</c>.</summary>
    private unsafe void TryCopy(int width, int height, long ticks)
    {
        int size = checked(width * height * 4);
        if (_scratch.Length < size)
            _scratch = new byte[size];
        int rc;
        int w, h;
        fixed (byte* p = _scratch)
            rc = Native.psrs_sck_copy_latest(_handle, p, _scratch.Length, out w, out h, out ticks);
        if (rc != Ok || w != width || h != height)
            return; // a frame of another size arrived in between: the next read takes it

        int top = MacCoordinates.TopInsetRows(width, height, ClientRect());
        int rowBytes = width * 4;
        var pixels = new byte[rowBytes * (height - top)];
        Buffer.BlockCopy(_scratch, top * rowBytes, pixels, 0, pixels.Length);
        _latest = new Frame(width, height - top, pixels);
        _latestTicks = ticks;
        _lastFrameTicks = ticks;
    }

    private PixelRect ClientRect()
    {
        var now = currentWindow?.Invoke();
        return now is not null && _window is not null && now.Handle == _window.Handle
            ? now.ClientScreenRect
            : _window?.ClientScreenRect ?? default;
    }

    /// <summary>
    /// Feeds a native result to the retry policy: logs the first failure and the recovery, restarts a failed capture
    /// after the backoff, and logs missing permission once per process. Caller holds <c>_gate</c>.
    /// </summary>
    private void Observe(int rc)
    {
        var status = rc switch
        {
            SizeOnly or Ok => CaptureStatus.Frame,
            NoFrame => CaptureStatus.NoFrame,
            NoPermission => CaptureStatus.NoPermission,
            WindowNotFound => CaptureStatus.WindowNotFound,
            _ => CaptureStatus.Failed,
        };
        if (status == CaptureStatus.NoPermission && Interlocked.Exchange(ref s_permissionLogged, 1) == 0)
            Log.Warn("Screen Recording permission is not granted: the Remote Play window cannot be captured");

        switch (_retry.Observe(status, DateTimeOffset.UtcNow))
        {
            case CaptureRetryAction.FirstFailure:
                Log.Warn(status == CaptureStatus.WindowNotFound
                    ? $"Screen capture: the Remote Play window is not shareable (yet); retrying every {RetryBackoff.TotalSeconds:0} s"
                    : $"Screen capture stream failed or stopped (code {rc}); retrying every {RetryBackoff.TotalSeconds:0} s");
                break;
            case CaptureRetryAction.Restart when _window is not null:
                StopCore();
                _handle = Native.psrs_sck_start(unchecked((uint)_window.Handle));
                break;
            case CaptureRetryAction.Recovered:
                Log.Info("Screen capture recovered");
                break;
        }
    }
}
