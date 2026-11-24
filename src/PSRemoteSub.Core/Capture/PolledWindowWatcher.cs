using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Capture;

/// <summary>
/// Window watcher driven by a probe function polled on a timer. <see cref="Changed"/> is raised (on the timer
/// thread, or the caller's thread for <see cref="Refresh"/> and <see cref="SetProbe"/>) only when the probe's
/// result differs from <see cref="Current"/>. A throwing probe counts as <c>null</c>.
/// </summary>
public sealed class PolledWindowWatcher(Func<WindowInfo?> probe, TimeSpan interval) : IWindowWatcher
{
    private readonly object _gate = new();
    private readonly object _refreshGate = new();
    private readonly LogThrottle _faults = new();
    private Func<WindowInfo?> _probe = probe;
    private Timer? _timer;
    private bool _disposed;
    private WindowInfo? _current;

    public WindowInfo? Current => Volatile.Read(ref _current);

    /// <summary>Raised whenever <see cref="Current"/> changes. Handlers must not throw.</summary>
    public event EventHandler<WindowInfo?>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
                throw new InvalidOperationException("Watcher already started.");
            _timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, interval);
        }
    }

    /// <summary>Swaps the probe and refreshes at once.</summary>
    public void SetProbe(Func<WindowInfo?> probe)
    {
        lock (_gate)
            _probe = probe;
        Refresh();
    }

    /// <summary>Runs the probe and raises <see cref="Changed"/> if the result differs from <see cref="Current"/>.</summary>
    public void Refresh()
    {
        // Whole refreshes are serialised (probe, commit and raise), so overlapping timer ticks or a SetProbe cannot
        // raise Changed out of order or let a stale probe result overwrite a newer one. Reentrant for handlers.
        lock (_refreshGate)
            RefreshCore();
    }

    private void RefreshCore()
    {
        Func<WindowInfo?> probeNow;
        lock (_gate)
        {
            if (_disposed)
                return;
            probeNow = _probe;
        }

        WindowInfo? info;
        try
        {
            info = probeNow();
            _faults.Reset();
        }
        catch (Exception ex)
        {
            info = null;
            if (_faults.ShouldLog(out int count))
                Log.Warn($"Window probe failed (#{count})", ex);
        }

        lock (_gate)
        {
            if (Equals(info, _current))
                return;
            Volatile.Write(ref _current, info);
        }
        Changed?.Invoke(this, info);
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
    }
}
