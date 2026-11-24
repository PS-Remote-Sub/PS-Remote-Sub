using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// The Windows <see cref="ICaptureCard"/>: a <see cref="CaptureCardEngine"/> (Media Foundation on an app-owned D3D11
/// device) with its <see cref="CaptureCardFrameSource"/>, <see cref="CaptureDeviceCatalog"/> for listing,
/// <see cref="WindowsVideoSurface"/> for presenting and <see cref="CaptureSurfaceProbe"/> for measuring.
/// </summary>
/// <remarks>
/// The constructor throws when the engine cannot be built (Media Foundation missing, or no hardware Direct3D 11 device
/// with video support). <see cref="Dispose"/> stops the presenters of surfaces still alive (their HWNDs go later, on
/// the UI thread), then the frame source, then the engine.
/// </remarks>
public sealed class WindowsCaptureCard : ICaptureCard
{
    private readonly CaptureCardEngine _engine;
    private readonly CaptureCardFrameSource _frames;
    private readonly Lock _surfacesGate = new();
    private readonly HashSet<WindowsVideoSurface> _surfaces = [];  // under _surfacesGate

    public WindowsCaptureCard()
    {
        _engine = new CaptureCardEngine();
        try
        {
            _frames = new CaptureCardFrameSource(_engine);
        }
        catch
        {
            _engine.Dispose();
            throw;
        }
    }

    public CaptureState State => _engine.State;

    public event EventHandler<CaptureState>? StateChanged
    {
        add => _engine.StateChanged += value;
        remove => _engine.StateChanged -= value;
    }

    public CaptureMode? CurrentMode => _engine.CurrentMode;

    public IReadOnlyList<CaptureMode> OfferedModes => _engine.OfferedModes;

    public DateTimeOffset? LastFrameAt => _engine.LastFrameAt;

    public IFrameSource Frames => _frames;

    public void Open(string deviceId, CaptureModeSetting? wanted) => _engine.Open(deviceId, wanted);

    public void Close() => _engine.Close();

    public Task<IReadOnlyList<CaptureDevice>> ListDevicesAsync() => CaptureDeviceCatalog.ListAsync();

    public IReadOnlyList<CaptureMode> ListModes(string deviceId) => CaptureDeviceCatalog.ListModes(deviceId);

    public ICaptureAudio CreateAudio() => new WindowsCaptureAudio();

    public IVideoSurface CreateSurface(nint parentHandle)
    {
        var surface = new WindowsVideoSurface(_engine, parentHandle, Forget);
        lock (_surfacesGate)
            _surfaces.Add(surface);
        return surface;
    }

    public WindowInfo? MeasurePicture(nint topLevel, IVideoSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var mode = _engine.CurrentMode;
        return CaptureSurfaceProbe.Measure(topLevel, surface.Handle, mode is null ? null : (mode.Width, mode.Height));
    }

    public void Dispose()
    {
        WindowsVideoSurface[] live;
        lock (_surfacesGate)
        {
            live = [.. _surfaces];
            _surfaces.Clear();
        }
        // A presenter draws from the engine: stop it first.
        foreach (var surface in live)
            surface.StopPresenting();
        _frames.Dispose();
        _engine.Dispose();
    }

    private void Forget(WindowsVideoSurface surface)
    {
        lock (_surfacesGate)
            _surfaces.Remove(surface);
    }
}
