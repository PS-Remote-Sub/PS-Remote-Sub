using Microsoft.Win32.SafeHandles;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Windows.Capture.Interop;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Shows the capture card's newest frame in a <see cref="VideoSurfaceWindow"/>, scaled to fit with black bars, through
/// a flip-model swap chain on the engine's device with one frame of latency.
/// </summary>
/// <remarks>
/// <para>
/// One thread (<c>"Capture card presenter"</c>) owns the swap chain and every view and video-processor object. Per
/// frame it waits on the swap chain's frame-latency waitable object (100 ms timeout), follows the surface's client
/// size (<c>ResizeBuffers</c>), and inside <see cref="CaptureCardEngine.UseLatest{T}"/> scales the newest frame into
/// the back buffer with <c>VideoProcessorBlt</c> (or clears it to black before the first frame), then
/// <c>Present(1, 0)</c>.
/// </para>
/// <para>
/// A <c>FlipDiscard</c> back buffer's content is undefined after a present, so every present is preceded by a fresh
/// blt of the newest frame; the frame-latency wait paces that to one per display refresh. With no frame, black is
/// presented once and then nothing until a frame or a resize arrives. A surface that is 0×0, gone or occluded
/// (minimised) is not presented to; the thread sleeps 100 ms between checks.
/// </para>
/// <para>
/// Failures (device lost, DXGI errors) are logged with a throttle; the swap chain and everything on it are released
/// and recreated on a later loop. Dispose this before the surface window and before the engine.
/// </para>
/// </remarks>
public sealed class VideoPresenter : IDisposable
{
    private const uint BufferCount = 2;
    private const Format BufferFormat = Format.B8G8R8A8_UNorm;
    private const SwapChainFlags ChainFlags = SwapChainFlags.FrameLatencyWaitableObject;
    private const int FrameWaitMs = 100;
    private const int IdleMs = 100;
    private const int ErrorBackoffMs = 500;
    private const int DxgiStatusOccluded = 0x087A0001;
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);
    private static readonly Color4 Black = new(0f, 0f, 0f, 1f);

    private readonly CaptureCardEngine _engine;
    private readonly nint _surface;
    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    private readonly LogThrottle _failures = new();
    private volatile bool _stopRequested;
    private readonly Action<ID3D11DeviceContext, ID3D11Texture2D>? _frameRendered;
    private int _disposed;
    private int _failureCount;

    // ---- Presenter thread only --------------------------------------------------------------------------------
    private IDXGISwapChain2? _swapChain;
    private WaitHandle? _frameLatency;              // the swap chain's frame-latency waitable object
    private bool _haveFrameSlot;                    // waited on _frameLatency and not yet presented
    private int _bufferWidth, _bufferHeight;
    private ID3D11Texture2D? _backBuffer;           // buffer 0: always the current back buffer in flip model
    private ID3D11RenderTargetView? _backBufferTarget;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;      // the engine's immediate context; use inside UseLatest only
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private (int InW, int InH, int OutW, int OutH) _processorSizes;
    private ID3D11VideoProcessorOutputView? _outputView;  // on _backBuffer, for _enumerator
    private ID3D11VideoProcessorInputView? _inputView;    // on _inputTexture, for _enumerator
    private nint _inputTexture;                     // kept alive by _inputView, so the pointer cannot be reused
    private bool _blackShown;                       // black presented at the current size and no frame since

    public VideoPresenter(CaptureCardEngine engine, nint surfaceHwnd)
        : this(engine, surfaceHwnd, null)
    {
    }

    /// <param name="frameRendered">
    /// Test hook: called inside <see cref="CaptureCardEngine.UseLatest{T}"/> after each draw, before the present. It is
    /// taken here, before the thread starts, so it cannot miss the first draw (the single black one when no frame comes).
    /// </param>
    internal VideoPresenter(CaptureCardEngine engine, nint surfaceHwnd, Action<ID3D11DeviceContext, ID3D11Texture2D>? frameRendered)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (surfaceHwnd == 0)
            throw new ArgumentException("The surface window handle is 0.", nameof(surfaceHwnd));
        _engine = engine;
        _surface = surfaceHwnd;
        _frameRendered = frameRendered;
        _thread = new Thread(Run) { IsBackground = true, Name = "Capture card presenter", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>How many loops failed (and were logged and recovered from); for tests.</summary>
    internal int FailureCount => Volatile.Read(ref _failureCount);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopRequested = true;
        _stop.Set();
        if (_thread.Join(JoinTimeout))
            _stop.Dispose();
        else
            Log.Warn("Capture card presenter did not stop within 2 s");
    }

    private void Run()
    {
        try
        {
            while (!_stopRequested)
            {
                int pauseMs;
                try
                {
                    pauseMs = RenderOnce();
                }
                catch (ObjectDisposedException ex) when (ex.ObjectName == typeof(CaptureCardEngine).FullName)
                {
                    Log.Warn("Capture card presenter stopping: the engine was disposed first");
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failureCount);
                    if (_failures.ShouldLog(out int count))
                        Log.Warn($"Capture card presenter failed (#{count}, 0x{ex.HResult:X8}); recreating the swap chain", ex);
                    ReleaseSwapChain();
                    pauseMs = ErrorBackoffMs;
                }
                if (pauseMs > 0 && !_stopRequested)
                    _stop.WaitOne(pauseMs);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Capture card presenter stopped unexpectedly", ex);
        }
        finally
        {
            ReleaseSwapChain();
            _videoContext?.Dispose();
            _videoContext = null;
            _videoDevice?.Dispose();
            _videoDevice = null;
        }
    }

    /// <summary>One loop: returns how long to sleep before the next (0 right after a present).</summary>
    private int RenderOnce()
    {
        if (_frameLatency is not null && !_haveFrameSlot)
        {
            // A timeout (e.g. nothing is being composed) just loops; the wait itself kept this from spinning.
            _haveFrameSlot = _frameLatency.WaitOne(FrameWaitMs);
            if (!_haveFrameSlot)
                return 0;
        }
        if (_stopRequested)
            return 0;

        if (!User32.IsWindow(_surface) || !User32.GetClientRect(_surface, out RECT client))
            return IdleMs;
        // A child keeps its size when its top-level window is minimised, and presents may still be paced as usual.
        if (User32.IsIconic(User32.GetAncestor(_surface, User32.GA_ROOT)))
            return IdleMs;
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            return IdleMs;

        if (_swapChain is null)
        {
            CreateSwapChain(width, height);
            return 0;  // wait for the new swap chain's first frame slot
        }
        if (width != _bufferWidth || height != _bufferHeight)
            ResizeSwapChain(width, height);

        bool drawn = _engine.UseLatest((context, texture, _) => Draw(context, texture));
        if (!drawn)
            return IdleMs;

        _haveFrameSlot = false;
        Result result = _swapChain.Present(1, PresentFlags.None);
        if (result.Code == DxgiStatusOccluded)
            return IdleMs;
        result.CheckError();
        _failures.Reset();
        return 0;
    }

    /// <summary>Inside <see cref="CaptureCardEngine.UseLatest{T}"/>: draws into the back buffer; false when there is nothing new to show.</summary>
    private bool Draw(ID3D11DeviceContext context, ID3D11Texture2D? texture)
    {
        if (texture is null)
        {
            if (_blackShown)
                return false;
            context.ClearRenderTargetView(_backBufferTarget!, Black);
            _blackShown = true;
        }
        else
        {
            Blt(context, texture);
            _blackShown = false;
        }
        _frameRendered?.Invoke(context, _backBuffer!);
        return true;
    }

    private void Blt(ID3D11DeviceContext context, ID3D11Texture2D texture)
    {
        Texture2DDescription d = texture.Description;
        int frameWidth = (int)d.Width, frameHeight = (int)d.Height;
        _videoDevice ??= _engine.Device.QueryInterface<ID3D11VideoDevice>();
        _videoContext ??= context.QueryInterface<ID3D11VideoContext>();
        EnsureProcessor(frameWidth, frameHeight);
        EnsureInputView(texture);

        ID3D11VideoProcessor processor = _processor!;
        PixelRect dest = Letterbox.Fit(new PixelRect(0, 0, _bufferWidth, _bufferHeight), frameWidth, frameHeight);
        _videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, _bufferWidth, _bufferHeight));
        _videoContext.VideoProcessorSetOutputBackgroundColor(processor, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });
        _videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, frameWidth, frameHeight));
        _videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(dest.X, dest.Y, dest.Right, dest.Bottom));

        var stream = new VideoProcessorStream { Enable = true, InputSurface = _inputView! };
        _videoContext.VideoProcessorBlt(processor, _outputView!, 0, 1, [stream]).CheckError();
    }

    /// <summary>(Re)creates the enumerator, processor and output view when the input or output size changed.</summary>
    private void EnsureProcessor(int frameWidth, int frameHeight)
    {
        var sizes = (frameWidth, frameHeight, _bufferWidth, _bufferHeight);
        if (_processor is not null && _processorSizes == sizes && _outputView is not null)
            return;
        ReleaseProcessor();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)frameWidth,
            InputHeight = (uint)frameHeight,
            OutputWidth = (uint)_bufferWidth,
            OutputHeight = (uint)_bufferHeight,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _videoDevice!.CreateVideoProcessorEnumerator(ref content, out _enumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor).CheckError();
        _processorSizes = sizes;

        // Plain scaling: no driver "enhancements" that cost time; full-range RGB in and out.
        _videoContext!.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        CreateOutputView();
    }

    private void CreateOutputView()
    {
        var description = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        _videoDevice!.CreateVideoProcessorOutputView(_backBuffer!, _enumerator!, description, out _outputView).CheckError();
    }

    private void EnsureInputView(ID3D11Texture2D texture)
    {
        if (_inputView is not null && _inputTexture == texture.NativePointer)
            return;
        _inputView?.Dispose();
        _inputView = null;
        var description = new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D };
        _videoDevice!.CreateVideoProcessorInputView(texture, _enumerator!, description, out _inputView).CheckError();
        _inputTexture = texture.NativePointer;
    }

    private void CreateSwapChain(int width, int height)
    {
        using var dxgiDevice = _engine.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = BufferFormat,
            BufferCount = BufferCount,
            BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = ChainFlags,
        };
        using (IDXGISwapChain1 swapChain1 = factory.CreateSwapChainForHwnd(_engine.Device, _surface, description))
            _swapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
        // The host window handles Alt+Enter and friends itself; DXGI must not.
        factory.MakeWindowAssociation(_surface, WindowAssociationFlags.IgnoreAll).CheckError();
        _swapChain.MaximumFrameLatency = 1;  // SetMaximumFrameLatency(1)
        _frameLatency = new NativeWaitHandle(_swapChain.FrameLatencyWaitableObject);
        _haveFrameSlot = false;
        _bufferWidth = width;
        _bufferHeight = height;
        CreateBackBufferViews();
        Log.Info($"Capture card presenter swap chain created at {width}x{height}");
    }

    private void ResizeSwapChain(int width, int height)
    {
        ReleaseBackBufferViews();
        _swapChain!.ResizeBuffers(BufferCount, (uint)width, (uint)height, BufferFormat, ChainFlags).CheckError();
        _bufferWidth = width;
        _bufferHeight = height;
        CreateBackBufferViews();
    }

    private void CreateBackBufferViews()
    {
        _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBufferTarget = _engine.Device.CreateRenderTargetView(_backBuffer);
        _blackShown = false;
    }

    /// <summary>
    /// Releases every reference to the back buffers (views, processor whose output view is on them) and flushes the
    /// immediate context so its deferred destruction lets go too, as <c>ResizeBuffers</c> requires.
    /// </summary>
    private void ReleaseBackBufferViews()
    {
        ReleaseProcessor();
        _backBufferTarget?.Dispose();
        _backBufferTarget = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
        try
        {
            _engine.UseLatest((context, _, _) =>
            {
                context.Flush();
                return true;
            });
        }
        catch (ObjectDisposedException)
        {
            // The engine is gone; nothing of ours is pending on its context any more.
        }
    }

    private void ReleaseProcessor()
    {
        _inputView?.Dispose();
        _inputView = null;
        _inputTexture = 0;
        _outputView?.Dispose();
        _outputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void ReleaseSwapChain()
    {
        try
        {
            ReleaseBackBufferViews();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture card presenter could not flush while releasing its swap chain", ex);
        }
        _frameLatency?.Dispose();
        _frameLatency = null;
        _haveFrameSlot = false;
        _swapChain?.Dispose();
        _swapChain = null;
        _bufferWidth = _bufferHeight = 0;
    }

    /// <summary>Owns a raw wait handle (the frame-latency waitable object, which the app must close).</summary>
    private sealed class NativeWaitHandle : WaitHandle
    {
        public NativeWaitHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }
}
