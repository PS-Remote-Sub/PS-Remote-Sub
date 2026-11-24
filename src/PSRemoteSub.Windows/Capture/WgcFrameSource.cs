using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Windows.Capture.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Captures a window with Windows.Graphics.Capture and publishes the client area as BGRA8 <see cref="Frame"/>s.
/// </summary>
/// <remarks>
/// Frames arrive on a thread-pool thread (free-threaded frame pool). Each one is only copied, on the GPU, into a
/// texture this class owns; the CPU readback (staging map + a new <see cref="Frame"/>) happens lazily in
/// <see cref="Latest"/>, at most once per arrived frame. So a 60 fps stream read every 300 ms costs ~3 readbacks a
/// second instead of 60 large-object allocations, and <see cref="Latest"/> returns the same object until a new frame
/// arrives (the pipeline relies on that to skip unchanged frames). The D3D immediate context is not thread-safe:
/// every use of it is under <c>_gate</c>.
/// </remarks>
public sealed class WgcFrameSource : IFrameSource
{
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;
    private const int BufferCount = 2;

    private readonly Lock _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly TypedEventHandler<Direct3D11CaptureFramePool, object> _frameArrivedHandler;
    private readonly LogThrottle _readbackFaults = new();
    private readonly LogThrottle _copyFaults = new();

    // All under _gate.
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _poolSize;
    private nint _hwnd;
    private ID3D11Texture2D? _gpuCopy;  // DEFAULT usage: latest client area, GPU only
    private readonly TextureReadback _readback;  // staging copy of _gpuCopy for the CPU
    private bool _dirty;                // _gpuCopy holds a frame newer than _latest
    private bool _disposed;
    private Frame? _latest;

    private long _lastFrameUtcTicks;
    private long _framesCaptured;

    public WgcFrameSource()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device,
            out _context).CheckError();
        _readback = new TextureReadback(_device);

        using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _winRtDevice = Direct3D11Interop.CreateDevice(dxgiDevice.NativePointer);
        _frameArrivedHandler = OnFrameArrived;
    }

    /// <summary>
    /// The latest client-area frame. Reads it back from the GPU only when a new frame has arrived since the last
    /// call; otherwise returns the same object. Thread-safe.
    /// </summary>
    public Frame? Latest
    {
        get
        {
            lock (_gate)
            {
                if (_dirty && !_disposed && _gpuCopy is { } gpuCopy)
                {
                    _dirty = false;
                    try
                    {
                        _latest = _readback.Read(_context, gpuCopy);
                        _readbackFaults.Reset();
                    }
                    catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
                    {
                        // Device lost or similar: keep the previous frame; the next arrival tries again.
                        if (_readbackFaults.ShouldLog(out int count))
                            Log.Warn($"Capture readback failed (#{count})", ex);
                    }
                }
                return _latest;
            }
        }
    }

    public DateTimeOffset? LastFrameAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastFrameUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Number of frames published since the last <see cref="Start"/> (diagnostics).</summary>
    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);

    public void Start(WindowInfo window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("Windows.Graphics.Capture is not supported on this system.");

        Stop();

        GraphicsCaptureItem item = GraphicsCaptureItemInterop.CreateForWindow(window.Handle);
        lock (_gate)
        {
            _latest = null;
            _dirty = false;
            Interlocked.Exchange(ref _lastFrameUtcTicks, 0);
            Interlocked.Exchange(ref _framesCaptured, 0);

            _hwnd = window.Handle;
            _poolSize = item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winRtDevice, PixelFormat, BufferCount, _poolSize);
            _pool.FrameArrived += _frameArrivedHandler;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            try
            {
                GraphicsCaptureSessionInterop.TrySetIsBorderRequired(_session, false);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException)
            {
                // Older Windows builds do not support borderless capture; the yellow border is cosmetic.
            }
            _session.StartCapture();
        }
    }

    public void Stop()
    {
        Direct3D11CaptureFramePool? pool;
        GraphicsCaptureSession? session;
        lock (_gate)
        {
            pool = _pool;
            session = _session;
            _pool = null;
            _session = null;
            _hwnd = 0;
            if (pool is not null)
                pool.FrameArrived -= _frameArrivedHandler;
        }
        // Disposed outside the lock so an in-flight FrameArrived callback waiting on _gate cannot deadlock Close().
        session?.Dispose();
        pool?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _readback.Dispose();
            _gpuCopy?.Dispose();
            _gpuCopy = null;
            _dirty = false;
            (_winRtDevice as IDisposable)?.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(sender, _pool) || _disposed)
                    return; // stopped or restarted

                using Direct3D11CaptureFrame? captured = sender.TryGetNextFrame();
                if (captured is null)
                    return;

                SizeInt32 contentSize = captured.ContentSize;
                if (contentSize.Width != _poolSize.Width || contentSize.Height != _poolSize.Height)
                {
                    // The surface is still sized for the old pool, so after a resize this frame's content may not
                    // fit it. Skip it rather than publish a partial, black or stale image.
                    _poolSize = contentSize;
                    sender.Recreate(_winRtDevice, PixelFormat, BufferCount, contentSize);
                    return;
                }

                if (CopyClientArea(captured, contentSize))
                {
                    _dirty = true;
                    Interlocked.Exchange(ref _lastFrameUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                    Interlocked.Increment(ref _framesCaptured);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let a capture callback fault escape into the WinRT frame pool. The stall backstop reports
            // a capture that stops delivering.
            if (_copyFaults.ShouldLog(out int count))
                Log.Warn($"Capture frame copy failed (#{count})", ex);
        }
    }

    /// <summary>
    /// GPU-copies the window's client area out of the captured surface into <c>_gpuCopy</c> (no CPU readback).
    /// Caller holds <c>_gate</c>.
    /// </summary>
    private bool CopyClientArea(Direct3D11CaptureFrame captured, SizeInt32 contentSize)
    {
        if (!TryGetClientBoxInCapture(contentSize, out int left, out int top, out int width, out int height))
            return false;

        using IDirect3DSurface surface = captured.Surface;
        using var source = new ID3D11Texture2D(Direct3D11Interop.GetDxgiInterface(surface, IID_ID3D11Texture2D));

        ID3D11Texture2D target = TextureReadback.EnsureTexture(
            _device, ref _gpuCopy, width, height, ResourceUsage.Default, CpuAccessFlags.None);
        _context.CopySubresourceRegion(target, 0, 0, 0, 0, source, 0, new Box(left, top, 0, left + width, top + height, 1));
        return true;
    }

    /// <summary>
    /// The capture covers the window's extended frame bounds; the client area sits at
    /// (client screen origin − frame bounds origin) inside it. Clipped to the captured content.
    /// </summary>
    private bool TryGetClientBoxInCapture(SizeInt32 contentSize, out int left, out int top, out int width, out int height)
    {
        left = top = width = height = 0;
        if (!User32.GetClientRect(_hwnd, out RECT client) || !Dwm.TryGetExtendedFrameBounds(_hwnd, out RECT bounds))
            return false;
        var origin = new POINT();
        if (!User32.ClientToScreen(_hwnd, ref origin))
            return false;

        left = Math.Max(0, origin.X - bounds.Left);
        top = Math.Max(0, origin.Y - bounds.Top);
        int right = Math.Min(contentSize.Width, origin.X - bounds.Left + (client.Right - client.Left));
        int bottom = Math.Min(contentSize.Height, origin.Y - bounds.Top + (client.Bottom - client.Top));
        width = right - left;
        height = bottom - top;
        return width > 0 && height > 0;
    }
}
