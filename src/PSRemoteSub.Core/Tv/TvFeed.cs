namespace PSRemoteSub.Core.Tv;

/// <summary>
/// What the TV should draw now: the settings plus one view per zone, and who is connected. Thread-safe; changes are
/// raised on <see cref="Changed"/> after the lock is released, only when something really changed.
/// </summary>
public sealed class TvFeed
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TvZoneView> _zones = [];
    private readonly List<string> _clients = [];
    private TvConfig _config = new(false, "", TvMode.Cover, 100, 0.85, 0, new TvCalibration(), false);

    public event EventHandler<TvFeedEvent>? Changed;

    public TvConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public void SetConfig(TvConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            if (_config == config)
                return;
            _config = config;
        }
        Raise(new TvConfigChanged(config));
    }

    /// <summary>Sets a zone's view (null clears it). Nothing is raised when the view is unchanged.</summary>
    public void SetZone(string zoneId, TvZoneView? view)
    {
        ArgumentNullException.ThrowIfNull(zoneId);
        lock (_gate)
        {
            if (view is null)
            {
                if (!_zones.Remove(zoneId))
                    return;
            }
            else
            {
                if (_zones.TryGetValue(zoneId, out var old) && old == view)
                    return;
                _zones[zoneId] = view;
            }
        }
        Raise(new TvZoneChanged(zoneId, view));
    }

    public IReadOnlyDictionary<string, TvZoneView> Zones()
    {
        lock (_gate)
            return new Dictionary<string, TvZoneView>(_zones);
    }

    public void ClientConnected(string remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        IReadOnlyList<string> snapshot;
        lock (_gate)
        {
            _clients.Add(remote);
            snapshot = [.. _clients];
        }
        Raise(new TvClientsChanged(snapshot));
    }

    public void ClientDisconnected(string remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        IReadOnlyList<string> snapshot;
        lock (_gate)
        {
            if (!_clients.Remove(remote))
                return;
            snapshot = [.. _clients];
        }
        Raise(new TvClientsChanged(snapshot));
    }

    public IReadOnlyList<string> Clients()
    {
        lock (_gate)
            return [.. _clients];
    }

    private void Raise(TvFeedEvent e) => Changed?.Invoke(this, e);
}
