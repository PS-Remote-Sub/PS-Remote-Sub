using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// The Windows <see cref="IVideoSurface"/>: a <see cref="VideoSurfaceWindow"/> (a native child HWND) with a
/// <see cref="VideoPresenter"/> drawing the capture card's picture into it for as long as it lives.
/// </summary>
/// <remarks>
/// Create and dispose it on the UI thread (the HWND's thread). Dispose stops the presenter before the HWND goes.
/// Its events are raised inside the surface's window procedure: a handler should post work that changes window state.
/// </remarks>
public sealed class WindowsVideoSurface : IVideoSurface
{
    private readonly VideoSurfaceWindow _window;
    private readonly Action<WindowsVideoSurface>? _disposed;
    private readonly Lock _presenterGate = new();
    private VideoPresenter? _presenter;  // under _presenterGate; null once stopped

    internal WindowsVideoSurface(CaptureCardEngine engine, nint parentHandle, Action<WindowsVideoSurface>? disposed = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _window = VideoSurfaceWindow.Create(parentHandle);
        _disposed = disposed;
        _window.KeyPressed += OnKeyPressed;
        _window.DoubleClicked += OnDoubleClicked;
        try
        {
            _presenter = new VideoPresenter(engine, _window.Handle);
        }
        catch (Exception ex)
        {
            // The surface stays (black) without a picture rather than failing the window.
            Log.Warn("Capture card presenter could not start", ex);
        }
    }

    public nint Handle => _window.Handle;

    public string HandleDescriptor => "HWND";

    public event EventHandler? DoubleClicked;

    public event EventHandler<VideoSurfaceKey>? KeyPressed;

    /// <summary>F11 toggles fullscreen and Esc leaves it; null for any other virtual-key code.</summary>
    internal static VideoSurfaceKey? MapKey(int vk) => vk switch
    {
        VideoSurfaceWindow.VK_F11 => VideoSurfaceKey.ToggleFullScreen,
        VideoSurfaceWindow.VK_ESCAPE => VideoSurfaceKey.LeaveFullScreen,
        _ => null,
    };

    /// <summary>Stops the presenter (any thread); the HWND stays until <see cref="Dispose"/>.</summary>
    internal void StopPresenting()
    {
        VideoPresenter? presenter;
        lock (_presenterGate)
        {
            presenter = _presenter;
            _presenter = null;
        }
        presenter?.Dispose();
    }

    public void Dispose()
    {
        // The presenter first: it draws into the HWND.
        try
        {
            StopPresenting();
        }
        finally
        {
            _window.KeyPressed -= OnKeyPressed;
            _window.DoubleClicked -= OnDoubleClicked;
            _window.Dispose();
            _disposed?.Invoke(this);
        }
    }

    private void OnKeyPressed(object? sender, int vk)
    {
        if (MapKey(vk) is { } key)
            KeyPressed?.Invoke(this, key);
    }

    private void OnDoubleClicked(object? sender, EventArgs e) => DoubleClicked?.Invoke(this, e);
}
