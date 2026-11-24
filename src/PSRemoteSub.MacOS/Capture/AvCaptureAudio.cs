using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.MacOS.Capture;

/// <summary>
/// The macOS <see cref="ICaptureAudio"/>: plays the capture card's audio input on the default output through
/// AVFoundation's audio preview output (CaptureCard.swift, <c>psrs_av_audio_*</c>).
/// </summary>
/// <remarks>
/// The native side starts asynchronously and reports whether it runs; an unknown device, or no microphone access, simply
/// never runs (the capture window shows the audio as missing). It is not restarted here when it stops (the device
/// unplugged): the capture window starts it again when the picture comes back. Never asks for microphone access.
/// <see cref="RunningChanged"/> is raised under this object's lock, in order: a handler must not block.
/// </remarks>
internal sealed class AvCaptureAudio : ICaptureAudio
{
    private static readonly ConcurrentDictionary<int, AvCaptureAudio> s_live = new();
    private static int s_nextId;

    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly Lock _gate = new();

    // Under _gate.
    private nint _handle;
    private int _attempt;
    private bool _running;
    private double _volume = 1;
    private bool _muted;
    private bool _disposed;

    public AvCaptureAudio() => s_live[_id] = this;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public event EventHandler<bool>? RunningChanged;

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
                ApplyLocked();
            }
        }
    }

    public bool Muted
    {
        get { lock (_gate) return _muted; }
        set
        {
            lock (_gate)
            {
                _muted = value;
                ApplyLocked();
            }
        }
    }

    public unsafe void Start(string audioDeviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(audioDeviceId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopLocked();
            _handle = Native.psrs_av_audio_start(audioDeviceId, _volume, _muted ? 1 : 0, _id, ++_attempt, &OnNativeRunning);
        }
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            StopLocked();
            _disposed = true;
        }
        s_live.TryRemove(_id, out _);
    }

    private void ApplyLocked()
    {
        if (_handle != 0)
            Native.psrs_av_audio_set(_handle, _volume, _muted ? 1 : 0);
    }

    /// <summary>Stops the native player (late reports from it are dropped) and reports not running.</summary>
    private void StopLocked()
    {
        if (_handle != 0)
        {
            Native.psrs_av_audio_stop(_handle);
            _handle = 0;
            _attempt++;
        }
        SetRunningLocked(false);
    }

    private void SetRunningLocked(bool running)
    {
        if (_running == running)
            return;
        _running = running;
        try
        {
            RunningChanged?.Invoke(this, running);
        }
        catch (Exception ex)
        {
            Log.Warn("Capture audio RunningChanged handler failed", ex);
        }
    }

    private void OnRunning(int attempt, bool running)
    {
        lock (_gate)
        {
            if (_disposed || _handle == 0 || attempt != _attempt)
                return;
            SetRunningLocked(running);
        }
    }

    // Called by native code on any thread. Must never throw.
    [UnmanagedCallersOnly]
    private static void OnNativeRunning(int owner, int attempt, int running)
    {
        try
        {
            if (s_live.TryGetValue(owner, out var audio))
                audio.OnRunning(attempt, running != 0);
        }
        catch (Exception ex)
        {
            try { Log.Warn($"Capture audio callback failed: {ex.Message}"); } catch { /* nothing more to do */ }
        }
    }
}
