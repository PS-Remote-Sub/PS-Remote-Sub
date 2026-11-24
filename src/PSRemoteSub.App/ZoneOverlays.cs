using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.App;

/// <summary>
/// One overlay window (and <see cref="OverlayController"/>) per zone of the active profile; routes lines by their zone
/// and applies the shared state to all. The first zone's box also shows the status line. UI thread only.
/// </summary>
internal sealed class ZoneOverlays(IOverlayWindowPlatform platform, Func<GameProfile> activeProfile,
    Func<PictureSource> pictureSource, Action<string, TvZoneView?> tvSink, Func<string> selectRegionHotkey)
{
    private readonly List<OverlayController> _controllers = [];
    private WindowInfo? _target;
    private PipelineStatus _status = PipelineStatus.Initial;
    private bool _paused;
    private bool _enabled = true;
    private bool _suppressed;
    private int? _maxWaiting;

    /// <summary>Makes the controllers match the active profile's zones: closes removed ones, adds new ones with the current state.</summary>
    public void Sync()
    {
        var zones = activeProfile().Zones;
        foreach (var gone in _controllers.Where(c => zones.All(z => z.Id != c.ZoneId)).ToList())
        {
            gone.Close();
            _controllers.Remove(gone);
        }
        foreach (var zone in zones)
        {
            if (_controllers.Any(c => c.ZoneId == zone.Id))
                continue;
            var added = new OverlayController(new OverlayWindow { OverlayPlatform = platform }, activeProfile, pictureSource,
                zone.Id, false, tvSink, selectRegionHotkey);
            added.SetStatus(_status);
            added.SetPaused(_paused);
            added.SetEnabled(_enabled);
            added.SetSuppressed(_suppressed);
            if (_maxWaiting is { } waiting)
                added.SetMaxWaiting(waiting);
            added.SetTarget(_target);
            _controllers.Add(added);
        }
        // The status line belongs to the profile's first zone, which may have changed.
        var firstId = zones.Count > 0 ? zones[0].Id : null;
        foreach (var c in _controllers)
        {
            c.ShowsStatus = c.ZoneId == firstId;
            c.Refresh();
        }
    }

    public void SetTarget(WindowInfo? target)
    {
        _target = target;
        foreach (var c in _controllers) c.SetTarget(target);
    }

    public void SetStatus(PipelineStatus status)
    {
        _status = status;
        foreach (var c in _controllers) c.SetStatus(status);
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        foreach (var c in _controllers) c.SetPaused(paused);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        foreach (var c in _controllers) c.SetEnabled(enabled);
    }

    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        foreach (var c in _controllers) c.SetSuppressed(suppressed);
    }

    public void SetMaxWaiting(int lines)
    {
        _maxWaiting = lines;
        foreach (var c in _controllers) c.SetMaxWaiting(lines);
    }

    public void Refresh()
    {
        foreach (var c in _controllers) c.Refresh();
    }

    public void Close()
    {
        foreach (var c in _controllers) c.Close();
        _controllers.Clear();
    }

    public void Show(SubtitleEntry entry) => For(entry.ZoneId)?.ShowSubtitle(entry);

    public void Refine(SubtitleEntry entry) => For(entry.ZoneId)?.RefineSubtitle(entry);

    /// <summary>The game subtitle went away in one zone, or (null) in all.</summary>
    public void Clear(string? zoneId)
    {
        if (zoneId is null)
            foreach (var c in _controllers) c.ClearSubtitle();
        else
            _controllers.FirstOrDefault(c => c.ZoneId == zoneId)?.ClearSubtitle();
    }

    public void HideZone(string zoneId) => _controllers.FirstOrDefault(c => c.ZoneId == zoneId)?.HideZone();

    // An entry without a zone goes to the first. An entry from a zone this profile does not have (a zone just removed,
    // or another profile's) is dropped: nothing would ever clear it there.
    private OverlayController? For(string? zoneId) =>
        zoneId is null ? _controllers.FirstOrDefault() : _controllers.FirstOrDefault(c => c.ZoneId == zoneId);
}
