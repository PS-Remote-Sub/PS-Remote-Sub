using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;
using Vortice.Direct3D11;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Hands the capture card's frames to the OCR pipeline. The engine keeps the newest frame on the GPU; this reads it
/// back only when a new one has arrived since the last read, and otherwise returns the same <see cref="Frame"/>
/// object (the pipeline relies on reference equality to skip unchanged frames).
/// </summary>
public sealed class CaptureCardFrameSource(CaptureCardEngine engine) : IFrameSource
{
    private readonly Lock _gate = new();
    private readonly TextureReadback _readback = new(engine.Device);
    private readonly LogThrottle _readbackFaults = new();

    // All under _gate.
    private bool _enabled;
    private bool _disposed;
    private long _sequence = -1;  // engine sequence of _latest
    private Frame? _latest;

    /// <summary>Enables readback. The window is ignored: the picture comes from the capture card.</summary>
    public void Start(WindowInfo window)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = true;
        }
    }

    /// <summary>Disables readback and drops <see cref="Latest"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _enabled = false;
            _latest = null;
            _sequence = -1;
        }
    }

    /// <summary>
    /// The newest frame. A new one is copied to the staging texture under the engine lock (a cheap GPU copy), then
    /// mapped and copied to memory outside it, so the presenter and the card's reader never wait for the CPU copy.
    /// </summary>
    public Frame? Latest
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled || _disposed)
                    return null;
                Copied? copied;
                try
                {
                    copied = engine.UseLatest(CopyIfNew);
                }
                catch (ObjectDisposedException)
                {
                    return null;  // the engine was disposed before this source
                }
                if (copied is { } c)
                    Read(c);
                return _latest;
            }
        }
    }

    public DateTimeOffset? LastFrameAt => engine.LastFrameAt;

    /// <summary>Disposes only the readback buffers; the engine belongs to the caller.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _latest = null;
            _readback.Dispose();
        }
    }

    /// <summary>
    /// Runs under the engine lock (and <c>_gate</c>): queues the GPU copy of a frame newer than <see cref="Latest"/>
    /// into the staging texture; null when there is nothing new (or the copy failed).
    /// </summary>
    private Copied? CopyIfNew(ID3D11DeviceContext context, ID3D11Texture2D? texture, long sequence)
    {
        if (texture is null)
        {
            // Nothing open (or no frame yet): never hand out a frame from an earlier session.
            _latest = null;
            _sequence = -1;
            return null;
        }
        if (sequence == _sequence)
            return null;

        try
        {
            var (width, height) = _readback.CopyToStaging(context, texture);
            return new Copied(context, width, height, sequence);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            Fault(ex);
            return null;
        }
    }

    /// <summary>Under <c>_gate</c>, outside the engine lock: maps the staging copy into <c>_latest</c>.</summary>
    private void Read(Copied copied)
    {
        try
        {
            _latest = _readback.ReadStaging(copied.Context, copied.Width, copied.Height);
            _sequence = copied.Sequence;
            _readbackFaults.Reset();
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            Fault(ex);
        }
    }

    /// <summary>Device lost or similar: keeps the previous frame; the next read tries again.</summary>
    private void Fault(Exception ex)
    {
        if (_readbackFaults.ShouldLog(out int count))
            Log.Warn($"Capture card readback failed (#{count})", ex);
    }

    /// <summary>A frame copied to the staging texture, waiting to be mapped.</summary>
    private readonly record struct Copied(ID3D11DeviceContext Context, int Width, int Height, long Sequence);
}
