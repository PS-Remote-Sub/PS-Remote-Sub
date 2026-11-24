using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// The macOS <see cref="IVideoSurface"/>: an NSView with an <c>AVCaptureVideoPreviewLayer</c> (aspect fit on black,
/// CaptureCard.swift) that AppKit draws the card's picture into straight from the capture session.
/// </summary>
/// <remarks>
/// Create and dispose it on the UI thread (the main thread on macOS). Its events are raised there, from the view's
/// mouse and key handling: a double-click, F11 and Esc. <see cref="AvCaptureCard"/> points it at each capture session it
/// opens (<see cref="Attach"/>, any thread). Dispose detaches it from its session before the view goes.
/// </remarks>
internal sealed class AvVideoSurface : IVideoSurface
{
    // View events from the native side (CaptureCard.swift).
    private const int DoubleClick = 0, ToggleFullScreen = 1, LeaveFullScreen = 2;

    private static readonly ConcurrentDictionary<int, AvVideoSurface> s_live = new();
    private static int s_nextId;

    private readonly int _id;
    private readonly Action<AvVideoSurface>? _disposed;
    private readonly Lock _gate = new();
    private nint _view;  // under _gate (written); 0 once disposed

    private AvVideoSurface(int id, nint view, Action<AvVideoSurface>? disposed)
    {
        _id = id;
        _view = view;
        _disposed = disposed;
    }

    /// <summary>Creates the view showing <paramref name="session"/> (0: black). UI thread only; throws when the view cannot be made.</summary>
    internal static unsafe AvVideoSurface Create(nint session, Action<AvVideoSurface>? disposed)
    {
        int id = Interlocked.Increment(ref s_nextId);
        nint view = Native.psrs_av_view_create(session, id, &OnNativeEvent);
        if (view == 0)
            throw new InvalidOperationException("The capture card view could not be created (it must be made on the main thread).");
        var surface = new AvVideoSurface(id, view, disposed);
        s_live[id] = surface;
        return surface;
    }

    public nint Handle => Volatile.Read(ref _view);

    public string HandleDescriptor => "NSView";

    public event EventHandler? DoubleClicked;

    public event EventHandler<VideoSurfaceKey>? KeyPressed;

    /// <summary>F11 toggles fullscreen and Esc leaves it; null for any other native event.</summary>
    internal static VideoSurfaceKey? MapKey(int kind) => kind switch
    {
        ToggleFullScreen => VideoSurfaceKey.ToggleFullScreen,
        LeaveFullScreen => VideoSurfaceKey.LeaveFullScreen,
        _ => null,
    };

    /// <summary>Points the view at capture session <paramref name="session"/> (0: none, black). Any thread.</summary>
    internal void Attach(nint session)
    {
        lock (_gate)
        {
            if (_view != 0)
                Native.psrs_av_view_attach(_view, session);
        }
    }

    /// <summary>
    /// Where the picture is on screen (Quartz global points), whether that is already the letterboxed picture (else the
    /// whole view), and whether the view's window is minimised. False when the view is gone or in no window. Any thread.
    /// </summary>
    internal bool TryMeasure(out double x, out double y, out double width, out double height, out bool letterboxed, out bool minimized)
    {
        lock (_gate)
        {
            x = y = width = height = 0;
            letterboxed = minimized = false;
            if (_view == 0 || Native.psrs_av_view_screen_rect(_view, out x, out y, out width, out height, out int flags) != 0)
                return false;
            letterboxed = (flags & 1) != 0;
            minimized = (flags & 2) != 0;
            return true;
        }
    }

    public void Dispose()
    {
        nint view;
        lock (_gate)
        {
            view = _view;
            _view = 0;
        }
        if (view == 0)
            return;
        s_live.TryRemove(_id, out _);
        try
        {
            _disposed?.Invoke(this);
        }
        finally
        {
            // Detaches the preview layer from its session, then removes and releases the view.
            Native.psrs_av_view_destroy(view);
        }
    }

    private void OnEvent(int kind)
    {
        if (kind == DoubleClick)
            DoubleClicked?.Invoke(this, EventArgs.Empty);
        else if (MapKey(kind) is { } key)
            KeyPressed?.Invoke(this, key);
    }

    // Called by native code on the main thread. Must never throw.
    [UnmanagedCallersOnly]
    private static void OnNativeEvent(int token, int kind)
    {
        try
        {
            if (s_live.TryGetValue(token, out var surface))
                surface.OnEvent(kind);
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Capture card view handler failed: {ex.Message}"); } catch { /* nothing more to do */ }
        }
    }
}
