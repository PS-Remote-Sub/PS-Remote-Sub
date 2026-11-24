using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// Hands the capture card's frames to the OCR pipeline. The native session keeps its newest frame (BGRA, sampled to
/// ~10 fps); this copies it out only when a new one has arrived since the last read, and otherwise returns the same
/// <see cref="Frame"/> object (the pipeline relies on reference equality to skip unchanged frames).
/// </summary>
internal sealed class AvFrameSource(AvCaptureCard card) : IFrameSource
{
    private readonly Lock _gate = new();

    // All under _gate.
    private bool _enabled;
    private bool _disposed;
    private long _ticks;  // arrival time of _latest
    private Frame? _latest;

    /// <summary>Enables reading. The window is ignored: the picture comes from the capture card.</summary>
    public void Start(WindowInfo window)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = true;
        }
    }

    /// <summary>Disables reading and drops <see cref="Latest"/>.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _enabled = false;
            _latest = null;
            _ticks = 0;
        }
    }

    public Frame? Latest
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled || _disposed)
                    return null;
                switch (card.TakeNewerFrame(_ticks, out var frame, out long ticks))
                {
                    case AvCaptureCard.FrameRead.NotOpen:
                        // Nothing open: never hand out a frame from an earlier session.
                        _latest = null;
                        _ticks = 0;
                        break;
                    case AvCaptureCard.FrameRead.Newer:
                        _latest = frame;
                        _ticks = ticks;
                        break;
                }
                return _latest;
            }
        }
    }

    public DateTimeOffset? LastFrameAt => card.LastFrameAt;

    /// <summary>Drops the frame; the card belongs to the caller.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _latest = null;
        }
    }
}
