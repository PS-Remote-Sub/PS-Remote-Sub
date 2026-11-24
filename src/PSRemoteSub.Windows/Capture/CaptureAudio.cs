using PSRemoteSub.Core.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace PSRemoteSub.Windows.Capture;

/// <summary>
/// Plays a capture card's audio input on the default output device with low latency (WinRT AudioGraph, lowest quantum
/// size). The graph resamples and absorbs clock drift between the card and the output device.
/// </summary>
/// <remarks>
/// All public members are safe to call from any thread and return quickly: <see cref="Start"/> queues the graph build
/// on the thread pool, where builds are serialized by a semaphore. A graph that hits an unrecoverable error (the card
/// unplugged, a sleep), or a change of the default render device, rebuilds the graph with the same device id after one
/// second, and a rebuild that fails is tried again every 3 s while playback is wanted (so audio comes back when the card
/// does; failures are logged throttled). A failed <see cref="Start"/> is not retried. <see cref="Stop"/> and
/// <see cref="Dispose"/> cancel any pending or in-flight build (a generation counter) and never rebuild afterwards.
/// <see cref="RunningChanged"/> is raised on whichever thread made the change, only when <see cref="IsRunning"/> changes.
/// </remarks>
public sealed class CaptureAudio : IDisposable
{
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogThrottle _failures = new(20);  // first failure, then every 20th (once a minute while retrying)

    // All fields below are guarded by _lock.
    private AudioGraph? _graph;
    private AudioDeviceOutputNode? _output;
    private string? _deviceId;
    private bool _wanted;
    private bool _subscribed;
    private bool _disposed;
    private bool _running;
    private bool _draining;
    private readonly Queue<bool> _events = new();
    private int _generation;
    private double _volume = 1;
    private bool _muted;

    public event EventHandler<bool>? RunningChanged;

    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    /// <summary>Output volume from 0 to 1; values outside the range are clamped.</summary>
    public double Volume
    {
        get { lock (_lock) return _volume; }
        set
        {
            lock (_lock)
            {
                _volume = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
                ApplyGainLocked();
            }
        }
    }

    public bool Muted
    {
        get { lock (_lock) return _muted; }
        set
        {
            lock (_lock)
            {
                _muted = value;
                ApplyGainLocked();
            }
        }
    }

    /// <summary>Starts (or restarts) playing the capture endpoint <paramref name="audioDeviceId"/>.</summary>
    public void Start(string audioDeviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(audioDeviceId);
        AudioGraph? old;
        lock (_lock)
        {
            if (_disposed)
                return;
            _deviceId = audioDeviceId;
            _wanted = true;
            SubscribeLocked();
            old = DetachGraphLocked();
            ScheduleBuildLocked(TimeSpan.Zero);
        }
        Release(old);
    }

    /// <summary>Stops playback and cancels any pending rebuild. <see cref="IsRunning"/> is false when this returns.</summary>
    public void Stop()
    {
        AudioGraph? old;
        lock (_lock)
        {
            _wanted = false;
            _deviceId = null;
            UnsubscribeLocked();
            old = DetachGraphLocked();
        }
        Release(old);
    }

    public void Dispose()
    {
        AudioGraph? old;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _wanted = false;
            _deviceId = null;
            UnsubscribeLocked();
            old = DetachGraphLocked();
        }
        Release(old);
        // _gate is deliberately left undisposed: an in-flight build may still release it.
    }

    // Detaches the current graph and invalidates every pending or in-flight build. Caller holds _lock.
    private AudioGraph? DetachGraphLocked()
    {
        _generation++;
        AudioGraph? old = _graph;
        _graph = null;
        _output = null;
        SetRunningLocked(false);
        return old;
    }

    // Records a state change and queues its event in the same critical section, so events keep transition order.
    private void SetRunningLocked(bool running)
    {
        if (_running == running)
            return;
        _running = running;
        _events.Enqueue(running);
    }

    // Disposes a detached graph off the caller's thread, and reports the running change.
    private void Release(AudioGraph? old)
    {
        DrainEvents();
        if (old is not null)
            Task.Run(() => DisposeGraph(old));
    }

    private static void DisposeGraph(AudioGraph graph)
    {
        try
        {
            graph.Stop();
            graph.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Capture audio graph dispose failed", ex);
        }
    }

    private void SubscribeLocked()
    {
        if (_subscribed)
            return;
        MediaDevice.DefaultAudioRenderDeviceChanged += OnDefaultRenderDeviceChanged;
        _subscribed = true;
    }

    private void UnsubscribeLocked()
    {
        if (!_subscribed)
            return;
        MediaDevice.DefaultAudioRenderDeviceChanged -= OnDefaultRenderDeviceChanged;
        _subscribed = false;
    }

    private void ApplyGainLocked()
    {
        if (_output is null)
            return;
        try
        {
            _output.OutgoingGain = _muted ? 0 : _volume;
        }
        catch (Exception ex)
        {
            Log.Warn("Capture audio gain change failed", ex);
        }
    }

    // Caller holds _lock. Bumps the generation so older builds discard their graph. With retryOnFailure, a failed build
    // schedules another one (still wanted and not superseded) after RetryDelay.
    private void ScheduleBuildLocked(TimeSpan delay, bool retryOnFailure = false)
    {
        int generation = ++_generation;
        _ = Task.Run(() => BuildAsync(generation, delay, retryOnFailure));
    }

    private async Task BuildAsync(int generation, TimeSpan delay, bool retryOnFailure)
    {
        bool failed = false;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay).ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string? deviceId;
                lock (_lock)
                    deviceId = _wanted && generation == _generation ? _deviceId : null;
                if (deviceId is not null)
                    failed = !await BuildGraphAsync(generation, deviceId).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            failed = true;
            Warn("Capture audio start failed", ex);
        }

        if (failed && retryOnFailure)
        {
            lock (_lock)
            {
                if (_wanted && !_disposed && generation == _generation)
                    ScheduleBuildLocked(RetryDelay, retryOnFailure: true);
            }
        }
    }

    /// <summary>A build failure, throttled: a card that stays unplugged retries every 3 s.</summary>
    private void Warn(string message, Exception? ex = null)
    {
        if (_failures.ShouldLog(out int count))
            Log.Warn(count == 1 ? message : $"{message} (#{count})", ex);
    }

    /// <summary>False when the graph could not be built; true when it runs or was no longer wanted.</summary>
    private async Task<bool> BuildGraphAsync(int generation, string deviceId)
    {
        AudioGraph? graph = null;
        bool handedOver = false;
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
            };
            CreateAudioGraphResult graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
            {
                Warn($"Capture audio graph creation failed: {graphResult.Status}", graphResult.ExtendedError);
                return false;
            }
            graph = graphResult.Graph;

            CreateAudioDeviceOutputNodeResult outputResult = await graph.CreateDeviceOutputNodeAsync();
            if (outputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Warn($"Capture audio output node failed: {outputResult.Status}", outputResult.ExtendedError);
                return false;
            }

            DeviceInformation device = await DeviceInformation.CreateFromIdAsync(deviceId);
            CreateAudioDeviceInputNodeResult inputResult =
                await graph.CreateDeviceInputNodeAsync(MediaCategory.Other, graph.EncodingProperties, device);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                // For example AccessDenied when the microphone privacy setting blocks the card's audio input.
                Warn($"Capture audio input node failed: {inputResult.Status}", inputResult.ExtendedError);
                return false;
            }

            inputResult.DeviceInputNode.AddOutgoingConnection(outputResult.DeviceOutputNode);
            AudioGraph built = graph;
            built.UnrecoverableErrorOccurred += (_, args) => OnGraphError(built, args.Error);

            lock (_lock)
            {
                if (!_wanted || generation != _generation)
                    return true; // Stopped, disposed or superseded while building; the finally block disposes the graph.
                AudioDeviceOutputNode output = outputResult.DeviceOutputNode;
                output.OutgoingGain = _muted ? 0 : _volume;
                built.Start();
                _graph = built;
                _output = output;
                handedOver = true;
                SetRunningLocked(true);
            }

            _failures.Reset();
            Log.Info($"Capture audio running: {built.EncodingProperties.SampleRate} Hz, "
                + $"{built.SamplesPerQuantum} samples/quantum, latency {built.LatencyInSamples} samples");
            DrainEvents();
            return true;
        }
        catch (Exception ex)
        {
            Warn("Capture audio graph build failed", ex);
            return false;
        }
        finally
        {
            if (!handedOver && graph is not null)
                DisposeGraph(graph);
        }
    }

    private void OnGraphError(AudioGraph graph, AudioGraphUnrecoverableError error)
    {
        Log.Warn($"Capture audio graph error: {error}; rebuilding");
        Rebuild(graph);
    }

    private void OnDefaultRenderDeviceChanged(object? sender, DefaultAudioRenderDeviceChangedEventArgs args)
    {
        if (args.Role != AudioDeviceRole.Default)
            return;
        Log.Info("Default audio output changed; rebuilding capture audio");
        Rebuild(null);
    }

    // Rebuilds with the same device id after a delay. A null graph means "whatever graph is current".
    private void Rebuild(AudioGraph? failed)
    {
        AudioGraph? old;
        lock (_lock)
        {
            if (!_wanted || _disposed || (failed is not null && !ReferenceEquals(failed, _graph)))
                return;
            old = DetachGraphLocked();
            ScheduleBuildLocked(RebuildDelay, retryOnFailure: true);
        }
        Release(old);
    }

    // Raises queued events in order. One drainer at a time, so a late "true" can never overtake a later "false".
    private void DrainEvents()
    {
        lock (_lock)
        {
            if (_draining)
                return;
            _draining = true;
        }
        while (true)
        {
            bool running;
            lock (_lock)
            {
                if (!_events.TryDequeue(out running))
                {
                    _draining = false;
                    return;
                }
            }
            try
            {
                RunningChanged?.Invoke(this, running);
            }
            catch (Exception ex)
            {
                Log.Warn("Capture audio RunningChanged handler failed", ex);
            }
        }
    }
}
