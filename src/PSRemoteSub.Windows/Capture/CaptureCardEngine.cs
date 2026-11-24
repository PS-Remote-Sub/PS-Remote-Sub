using System.Runtime.InteropServices;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Settings;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Opens a capture card with a Media Foundation Source Reader on an app-owned D3D11 device and keeps the newest frame
/// as a BGRA texture on the GPU.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="Open"/> starts one read thread (<c>"Capture card reader"</c>) that opens the device, tracks
/// <see cref="State"/>, retries failures every 3 s and tears the reader down. The reader runs in asynchronous mode: a
/// synchronous <c>ReadSample</c> cannot be cancelled (on the test card, <c>Flush</c>, stream deselection and
/// <c>IMFMediaSource::Shutdown</c> from another thread all leave it blocked until the next frame, or for ever after
/// a shutdown), so a card that sends nothing could never be closed or fall back. The reader's callback GPU-copies
/// each sample into the engine-owned texture under the engine lock, releases it at once, and asks for the next one.
/// </para>
/// <para>
/// The engine lock guards the newest-frame texture, its sequence number and every use of the immediate context that
/// touches them (<see cref="UseLatest{T}"/>). Media Foundation's own use of the device, and a reader mapping its own
/// staging copy outside the lock (<see cref="CaptureCardFrameSource"/>), are serialised by <c>ID3D11Multithread</c>.
/// </para>
/// </remarks>
public sealed class CaptureCardEngine : IDisposable
{
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const int MfEShutdown = unchecked((int)0xC00D3E85);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    private const int MfEEndOfStream = unchecked((int)0xC00D3E84);
    private const int EFail = unchecked((int)0x80004005);

    private const long NoSignalAfterMs = 2000;
    private const int TickMs = 100;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(5);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IMFDXGIDeviceManager _manager;

    private readonly Lock _gate = new();       // the engine lock: immediate context, _latest, _sequence, _disposed
    private readonly Lock _lifecycle = new();  // serialises Open / Close / Dispose
    private readonly Lock _stateGate = new();  // state changes and the queue of their StateChanged events
    private readonly Queue<CaptureState> _pendingEvents = new();  // under _stateGate
    private int _dispatchingThreadId;                              // under _stateGate; 0 when nobody is raising events

    private ID3D11Texture2D? _latest;  // under _gate
    private long _sequence;            // under _gate
    private bool _disposed;            // under _gate
    private Session? _session;         // under _lifecycle

    private volatile CaptureState _state = CaptureState.NoDevice;
    private volatile CaptureMode? _currentMode;
    private volatile IReadOnlyList<CaptureMode> _offeredModes = [];
    private long _lastFrameUtcTicks;

    public CaptureCardEngine()
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out _device,
                out _context).CheckError();

            using (var multithread = _context.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);

            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(_device).CheckError();
        }
        catch
        {
            _context?.Dispose();
            _device?.Dispose();
            MediaFactory.MFShutdown();
            throw;
        }
    }

    public CaptureState State => _state;

    /// <summary>
    /// Raised when <see cref="State"/> changes, and only then: normally on the read thread
    /// (<see cref="CaptureState.NoDevice"/> after <see cref="Close"/> on the closing thread). Events arrive one at a
    /// time and in order, and no engine lock is held while a handler runs. Handlers must not call <see cref="Open"/>,
    /// <see cref="Close"/> or <see cref="Dispose"/> synchronously (that throws); post to another thread instead.
    /// </summary>
    public event EventHandler<CaptureState>? StateChanged;

    /// <summary>The mode being read, or null when nothing is open.</summary>
    public CaptureMode? CurrentMode => _currentMode;

    /// <summary>The native modes of the device last opened.</summary>
    public IReadOnlyList<CaptureMode> OfferedModes => _offeredModes;

    public DateTimeOffset? LastFrameAt
    {
        get
        {
            long ticks = Interlocked.Read(ref _lastFrameUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>The app-owned D3D11 device the frames live on.</summary>
    public ID3D11Device Device => _device;

    /// <summary>
    /// Opens <paramref name="deviceId"/> (closing what was open). Returns at once; the work runs on the read thread.
    /// </summary>
    public void Open(string deviceId, CaptureModeSetting? wanted)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ThrowIfInsideStateChanged();
        lock (_lifecycle)
        {
            lock (_gate)
                ObjectDisposedException.ThrowIf(_disposed, this);
            CloseCore();

            var session = new Session(deviceId, wanted);
            var thread = new Thread(() => Run(session)) { IsBackground = true, Name = "Capture card reader" };
            session.Thread = thread;
            _session = session;
            thread.Start();
        }
    }

    /// <summary>Stops the read thread and releases the device so another app can open it.</summary>
    public void Close()
    {
        ThrowIfInsideStateChanged();
        lock (_lifecycle)
            CloseCore();
    }

    /// <summary>
    /// Runs <paramref name="use"/> under the engine lock with the immediate context, the newest-frame texture (null
    /// before the first frame) and its sequence number (+1 per frame).
    /// </summary>
    public T UseLatest<T>(Func<ID3D11DeviceContext, ID3D11Texture2D?, long, T> use)
    {
        ArgumentNullException.ThrowIfNull(use);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return use(_context, _latest, _sequence);
        }
    }

    public void Dispose()
    {
        ThrowIfInsideStateChanged();
        lock (_lifecycle)
        {
            CloseCore();
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            _manager.Dispose();
            _context.Dispose();
            _device.Dispose();
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// A handler calling these synchronously could be on the read thread, which they join; refuse that rather than
    /// stall for the join timeout.
    /// </summary>
    private void ThrowIfInsideStateChanged()
    {
        lock (_stateGate)
        {
            if (_dispatchingThreadId == Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Do not open, close or dispose the capture engine from a StateChanged handler.");
        }
    }

    /// <summary>Caller holds <c>_lifecycle</c>.</summary>
    private void CloseCore()
    {
        Session? session = _session;
        _session = null;
        if (session is null)
            return;

        session.RequestStop();
        if (session.Thread.Join(JoinTimeout))
            session.Wake.Dispose();
        else
            Log.Warn("Capture card reader did not stop within 5 s");

        lock (_gate)
        {
            _latest?.Dispose();
            _latest = null;
        }
        _currentMode = null;
        lock (_stateGate)
        {
            if (_state != CaptureState.NoDevice)
            {
                _state = CaptureState.NoDevice;
                _pendingEvents.Enqueue(CaptureState.NoDevice);
            }
        }
        DispatchStateEvents();
    }

    /// <summary>Sets the state for <paramref name="session"/> unless it has been stopped, raising <see cref="StateChanged"/> on change.</summary>
    private void SetState(Session session, CaptureState state)
    {
        lock (_stateGate)
        {
            if (session.StopRequested || _state == state)
                return;
            _state = state;
            _pendingEvents.Enqueue(state);
        }
        DispatchStateEvents();
    }

    /// <summary>
    /// Raises the queued <see cref="StateChanged"/> events in order, with no lock held during a handler. Only one
    /// thread dispatches at a time; a thread that finds another dispatching leaves its event to that one.
    /// </summary>
    private void DispatchStateEvents()
    {
        lock (_stateGate)
        {
            if (_dispatchingThreadId != 0)
                return;
            _dispatchingThreadId = Environment.CurrentManagedThreadId;
        }
        while (true)
        {
            CaptureState next;
            lock (_stateGate)
            {
                if (!_pendingEvents.TryDequeue(out next))
                {
                    _dispatchingThreadId = 0;
                    return;
                }
            }
            try
            {
                StateChanged?.Invoke(this, next);
            }
            catch (Exception ex)
            {
                Log.Warn("Capture card StateChanged handler failed", ex);
            }
        }
    }

    // ---- Read thread ------------------------------------------------------------------------------------------

    private void Run(Session session)
    {
        SetState(session, CaptureState.Opening);
        var fallback = new CaptureFallback();
        CaptureMode? forced = null;  // the MJPG fallback while chosen

        while (!session.StopRequested)
        {
            var step = CaptureAttemptStep.KeepReading;
            try
            {
                step = RunAttempt(session, forced, fallback);
            }
            catch (Exception ex) when (!session.StopRequested)
            {
                int hr = ex.HResult;
                CaptureState state = CaptureErrors.Classify(hr);
                LogFailure(session, hr, state, ex);
                SetState(session, state);
            }
            catch (Exception)
            {
                // Closing: whatever failed no longer matters.
            }

            if (session.StopRequested)
                break;

            if (step == CaptureAttemptStep.FallBack && _currentMode is { } failed
                && CaptureModeChooser.Fallback(_offeredModes, failed) is { } mjpg)
            {
                Log.Info($"No frames from {failed} within 3 s; reopening as {mjpg}");
                forced = mjpg;
                SetState(session, CaptureState.Opening);
                continue;
            }
            if (step == CaptureAttemptStep.RetryPreferred)
            {
                // Both formats were silent: that was no signal, not the mode. Try the preferred one again now.
                Log.Info($"Frames from {forced} after both formats were silent; reopening the preferred mode");
                forced = null;
                SetState(session, CaptureState.Opening);
                continue;
            }

            session.Wake.WaitOne(RetryEvery);
        }
    }

    /// <summary>
    /// Logs a failed attempt. A failure that repeats every 3 s (a card in use, a broken driver) is logged the first time,
    /// then every 20th (once a minute), so the rotated log keeps the startup lines; a new HRESULT, or a failure after the
    /// card played, starts over.
    /// </summary>
    private void LogFailure(Session session, int hr, CaptureState state, Exception ex)
    {
        if (hr != session.LastFailure || _state == CaptureState.Playing)
        {
            session.LastFailure = hr;
            session.Failures.Reset();
        }
        if (!session.Failures.ShouldLog(out int count))
            return;
        Log.Warn(count == 1
            ? $"Capture card error 0x{hr:X8} classified as {state}"
            : $"Capture card error 0x{hr:X8} classified as {state} (#{count} in a row)", ex);
        session.LogNextOpen = true;
    }

    /// <summary>
    /// One open-and-read attempt. Returns <see cref="CaptureAttemptStep.FallBack"/> or
    /// <see cref="CaptureAttemptStep.RetryPreferred"/> to reopen in another mode (<see cref="CaptureFallback"/>),
    /// <see cref="CaptureAttemptStep.KeepReading"/> when stopped or when the device is missing; throws on any Media
    /// Foundation failure.
    /// </summary>
    private CaptureAttemptStep RunAttempt(Session session, CaptureMode? forced, CaptureFallback fallback)
    {
        var devices = CaptureDeviceCatalog.ListAsync().GetAwaiter().GetResult();
        if (!devices.Any(d => string.Equals(d.Id, session.DeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            // A card that was unplugged mid-stream stays Unplugged until it comes back.
            SetState(session, _state == CaptureState.Unplugged ? CaptureState.Unplugged : CaptureState.NoDevice);
            return CaptureAttemptStep.KeepReading;
        }

        IMFMediaSource source;
        using (var sourceAttributes = MediaFactory.MFCreateAttributes(2))
        {
            sourceAttributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            sourceAttributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, session.DeviceId);
            source = MediaFactory.MFCreateDeviceSource(sourceAttributes);
        }

        var attempt = new Attempt(this, session);
        IMFSourceReader? reader = null;
        try
        {
            using (var readerAttributes = MediaFactory.MFCreateAttributes(4))
            {
                readerAttributes.Set(SourceReaderAttributeKeys.D3DManager, _manager);
                readerAttributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, 1u);
                readerAttributes.Set(SinkWriterAttributeKeys.LowLatency, 1u);
                readerAttributes.Set(SourceReaderAttributeKeys.AsyncCallback, attempt);
                reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, readerAttributes);
            }

            var native = ReadNativeModes(reader);
            var offered = native.Select(n => n.Mode).ToList();
            _offeredModes = offered;

            CaptureMode mode = forced ?? CaptureModeChooser.Choose(offered, session.Wanted)
                ?? throw new NotSupportedException("The capture device offers no usable video mode.");
            int index = native.First(n => n.Mode == mode).Index;
            string nativeDetails = SetMediaTypes(reader, index);
            _currentMode = mode;

            bool fallbackPossible = forced is null && CaptureModeChooser.Fallback(offered, mode) is not null;
            // Once per mode, not on every retry of a failure that repeats (see LogFailure). The colour attributes are
            // what a wrong-colour report needs (see CaptureColor).
            if (session.LogNextOpen || !Equals(mode, session.LastLoggedMode))
            {
                Log.Info($"Capture card reading {mode}: native {nativeDetails}");
                session.LastLoggedMode = mode;
                session.LogNextOpen = false;
            }
            attempt.Start(reader);
            return Watch(session, attempt, fallback, onFallback: forced is not null, fallbackPossible);
        }
        finally
        {
            if (reader is not null)
            {
                attempt.Stop(reader);
                reader.Dispose();
            }
            // Disposing the reader already shuts the source down, so MF_E_SHUTDOWN here is expected.
            ShutDown(source);
            source.Dispose();
            attempt.Dispose();
        }
    }

    /// <summary>
    /// Sets the device's native type, then a same-size BGRA output so the reader's GPU processor decodes MJPG,
    /// applies the YUV matrix and range, and hands out BGRA textures. Returns the native type's size, exact rate,
    /// subtype, YUV matrix and nominal range for the log (an unset matrix or range is the processor's default).
    /// </summary>
    private static string SetMediaTypes(IMFSourceReader reader, int nativeIndex)
    {
        using IMFMediaType nativeType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, nativeIndex);
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, nativeType);

        MediaFactory.MFGetAttributeSize(nativeType, MediaTypeAttributeKeys.FrameSize, out uint width, out uint height);
        MediaFactory.MFGetAttributeRatio(nativeType, MediaTypeAttributeKeys.FrameRate, out uint num, out uint den);
        string details = $"{width}x{height} at {num}/{den} fps, subtype {nativeType.GetGUID(MediaTypeAttributeKeys.Subtype)}, "
            + CaptureColor.Describe(
                TryGetUInt32(nativeType, MediaTypeAttributeKeys.YuvMatrix),
                TryGetUInt32(nativeType, MediaTypeAttributeKeys.VideoNominalRange));
        using IMFMediaType output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        // ARGB32 maps to DXGI B8G8R8A8_UNorm; RGB32 maps to B8G8R8X8_UNorm, which cannot be copied into _latest.
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
        MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, width, height).CheckError();
        MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, num, den).CheckError();
        reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        return details;
    }

    /// <summary>A UINT32 attribute, or null when the type does not set it.</summary>
    private static uint? TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (SharpGenException)
        {
            return null; // MF_E_ATTRIBUTENOTFOUND (or the wrong type): unset
        }
    }

    /// <summary>
    /// Watches a running attempt on the read thread until it is stopped (<see cref="CaptureAttemptStep.KeepReading"/>),
    /// fails (throws), or should reopen in another mode (<paramref name="fallback"/> decides). Playing on a frame,
    /// NoSignal after 2 s without one (not while an uncompressed mode may still fall back).
    /// </summary>
    private CaptureAttemptStep Watch(
        Session session, Attempt attempt, CaptureFallback fallback, bool onFallback, bool fallbackPossible)
    {
        long startedAt = Environment.TickCount64;
        while (true)
        {
            WaitHandle.WaitAny([session.Wake, attempt.Signal], TickMs);
            if (session.StopRequested)
                return CaptureAttemptStep.KeepReading;
            if (attempt.FailedWith is int hr)
                throw new COMException("The capture card reader reported a failure.", hr);

            long now = Environment.TickCount64;
            long lastFrameAt = attempt.LastFrameAt;
            var step = fallback.Next(onFallback, fallbackPossible, now - startedAt, hadFrame: lastFrameAt != 0);
            if (step != CaptureAttemptStep.KeepReading)
                return step;

            if (lastFrameAt != 0 && now - lastFrameAt < NoSignalAfterMs)
                SetState(session, CaptureState.Playing);
            else if (lastFrameAt == 0 && fallbackPossible)
            {
                // Silent so far: the fallback decision comes first.
            }
            else if (now - Math.Max(startedAt, lastFrameAt) >= NoSignalAfterMs)
                SetState(session, CaptureState.NoSignal);
        }
    }

    // ---- Reader callback (Media Foundation work queue thread) ------------------------------------------------

    /// <summary>GPU-copies a sample's texture into <c>_latest</c>; false when the session has been stopped.</summary>
    private bool Publish(Session session, IMFSample sample, int width, int height, bool firstFrame)
    {
        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
        using var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(IID_ID3D11Texture2D));
        uint subresource = dxgiBuffer.SubresourceIndex;

        lock (_gate)
        {
            if (session.StopRequested || _disposed)
                return false;
            Texture2DDescription d = texture.Description;
            if (firstFrame)
                Log.Info($"Capture card first frame: {d.Width}x{d.Height} {d.Format}, array {d.ArraySize}, subresource {subresource}");
            // A copy between format groups (e.g. B8G8R8X8 → B8G8R8A8) is silently dropped by D3D11, leaving black
            // frames, so refuse anything else loudly (classified Failed and retried).
            if (d.Format != Format.B8G8R8A8_UNorm)
                throw new NotSupportedException($"The capture card reader produced {d.Format} textures, not B8G8R8A8_UNorm.");
            int w = Math.Min(width, (int)d.Width), h = Math.Min(height, (int)d.Height);
            ID3D11Texture2D target = TextureReadback.EnsureTexture(
                _device, ref _latest, w, h, ResourceUsage.Default, CpuAccessFlags.None,
                BindFlags.ShaderResource | BindFlags.RenderTarget);
            _context.CopySubresourceRegion(target, 0, 0, 0, 0, texture, subresource, new Box(0, 0, 0, w, h, 1));
            _sequence++;
        }
        Interlocked.Exchange(ref _lastFrameUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        return true;
    }

    private static List<(CaptureMode Mode, int Index)> ReadNativeModes(IMFSourceReader reader)
    {
        var modes = new List<(CaptureMode, int)>();
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i);
            }
            catch (SharpGenException ex) when (ex.HResult == MfENoMoreTypes)
            {
                return modes;
            }
            using (type)
                modes.Add((CaptureDeviceCatalog.ToMode(type), i));
        }
    }

    private static (int Width, int Height) OutputSize(IMFSourceReader reader)
    {
        using IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
        return ((int)w, (int)h);
    }

    private static void ShutDown(IMFMediaSource source)
    {
        try { source.Shutdown(); }
        catch (SharpGenException ex) when (ex.HResult == MfEShutdown) { }
    }

    /// <summary>
    /// Asks an asynchronous reader for the next video sample. Vortice's <c>ReadSample</c> always passes its out
    /// pointers, which asynchronous mode rejects (they must be null), so this calls the vtable slot directly.
    /// </summary>
    private static unsafe void RequestSample(IMFSourceReader reader)
    {
        const int ReadSampleSlot = 9;  // IUnknown (3), Get/SetStreamSelection, Get/Native/Current/SetCurrentMediaType, SetCurrentPosition
        var readSample = (delegate* unmanaged[Stdcall]<nint, int, int, int*, int*, long*, nint*, int>)
            (*(void***)reader.NativePointer)[ReadSampleSlot];
        int hr = readSample(reader.NativePointer, (int)SourceReaderIndex.FirstVideoStream, 0, null, null, null, null);
        new Result(hr).CheckError();
    }

    /// <summary>State shared by the read thread and <see cref="Close"/> for one <see cref="Open"/>.</summary>
    private sealed class Session(string deviceId, CaptureModeSetting? wanted)
    {
        private volatile bool _stopRequested;

        public string DeviceId { get; } = deviceId;
        public CaptureModeSetting? Wanted { get; } = wanted;
        public Thread Thread { get; set; } = null!;
        public EventWaitHandle Wake { get; } = new ManualResetEvent(false);
        public bool StopRequested => _stopRequested;

        // Read thread only: throttles the logging of a failure that repeats on every retry.
        public LogThrottle Failures { get; } = new(20);
        public int LastFailure { get; set; }
        public CaptureMode? LastLoggedMode { get; set; }
        public bool LogNextOpen { get; set; } = true;  // the next attempt logs its mode (first, or after a logged failure)

        public void RequestStop()
        {
            _stopRequested = true;
            Wake.Set();
        }
    }

    /// <summary>
    /// The asynchronous reader callback for one open attempt. Copies each sample out and requests the next one;
    /// reports the first failure to the read thread through <see cref="Signal"/>.
    /// </summary>
    private sealed class Attempt(CaptureCardEngine engine, Session session) : IMFSourceReaderCallbackBase
    {
        private readonly Lock _requestGate = new();       // orders sample requests against Stop's flush
        private readonly ManualResetEventSlim _flushed = new();
        private readonly LogThrottle _events = new();
        private IMFSourceReader? _reader;                  // under _requestGate
        private bool _stopping;                            // under _requestGate
        private int _width, _height;                       // callback thread only
        private long _lastFrameAt;                         // Environment.TickCount64; 0 before the first frame
        private int _failedWith;                           // HRESULT; 0 while healthy

        /// <summary>Set on the first frame, on a frame after a 2 s gap, and on failure.</summary>
        public AutoResetEvent Signal { get; } = new(false);

        public long LastFrameAt => Interlocked.Read(ref _lastFrameAt);

        public int? FailedWith => Volatile.Read(ref _failedWith) is var hr and not 0 ? hr : null;

        /// <summary>Read thread: issues the first request.</summary>
        public void Start(IMFSourceReader reader)
        {
            (_width, _height) = OutputSize(reader);
            lock (_requestGate)
            {
                _reader = reader;
                RequestSample(reader);
            }
        }

        /// <summary>Read thread: stops requesting, cancels the pending request and waits for the flush to finish.</summary>
        public void Stop(IMFSourceReader reader)
        {
            lock (_requestGate)
            {
                _stopping = true;
                _reader = null;
            }
            try
            {
                reader.Flush(SourceReaderIndex.FirstVideoStream);
                if (!_flushed.Wait(FlushTimeout))
                    Log.Warn("Capture card reader flush did not complete within 2 s");
            }
            catch (Exception ex) when (ex is SharpGenException or COMException)
            {
                // A reader whose device failed may refuse to flush; it has nothing pending then.
            }
        }

        public override void OnReadSample(Result hrStatus, SourceReaderIndex streamIndex, SourceReaderFlag streamFlags, long timestamp, IMFSample sample)
        {
            try
            {
                if (hrStatus.Failure)
                {
                    Fail(hrStatus.Code);
                    return;
                }
                if ((streamFlags & SourceReaderFlag.Error) != 0)
                {
                    Fail(EFail);
                    return;
                }
                if ((streamFlags & SourceReaderFlag.EndOfStream) != 0)
                {
                    Fail(MfEEndOfStream);
                    return;
                }

                lock (_requestGate)
                {
                    if (_stopping || _reader is not { } reader)
                        return;
                    if ((streamFlags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
                        (_width, _height) = OutputSize(reader);
                    if (sample is not null && engine.Publish(session, sample, _width, _height, LastFrameAt == 0))
                    {
                        long now = Environment.TickCount64;
                        long previous = Interlocked.Exchange(ref _lastFrameAt, now);
                        if (previous == 0 || now - previous >= NoSignalAfterMs)
                            Signal.Set();
                    }
                    RequestSample(reader);
                }
            }
            catch (Exception ex)
            {
                Fail(ex.HResult);
            }
        }

        public override void OnFlush(SourceReaderIndex streamIndex) => _flushed.Set();

        /// <summary>
        /// A source or reader event with a failed status (e.g. the device going away, if it is reported this way
        /// rather than through <see cref="OnReadSample"/>) ends the attempt like a read failure: classified and
        /// retried. Other events are only logged (throttled) so real hardware shows what arrives.
        /// </summary>
        public override void OnEvent(SourceReaderIndex streamIndex, IMFMediaEvent mediaEvent)
        {
            try
            {
                if (mediaEvent is null)
                    return;
                Result status = mediaEvent.Status;
                if (status.Failure)
                {
                    Log.Warn($"Capture card reader event {mediaEvent.EventType} failed with 0x{status.Code:X8}");
                    Fail(status.Code);
                }
                else if (_events.ShouldLog(out int count))
                {
                    Log.Info($"Capture card reader event {mediaEvent.EventType} (#{count})");
                }
            }
            catch (Exception ex)
            {
                Fail(ex.HResult);
            }
        }

        private void Fail(int hr)
        {
            Interlocked.CompareExchange(ref _failedWith, hr == 0 ? EFail : hr, 0);
            Signal.Set();
        }

        // Signal and _flushed are left to the finalizer: Media Foundation may release its reference to this callback
        // (and so finish a late call into it) after the reader is gone.
    }
}
