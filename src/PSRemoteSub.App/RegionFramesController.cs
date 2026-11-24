using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>
/// Shows the debug outlines of every zone's boxes in the active profile over Remote Play while they are turned on. Independent of the
/// overlay toggle and of pause. UI thread only.
/// </summary>
internal sealed class RegionFramesController(RegionFramesWindow window, Func<GameProfile> activeProfile)
{
    private WindowInfo? _target;
    private bool _enabled;
    private bool _suppressed;
    private bool _closed;
    private string? _profileName;
    private readonly HashSet<string> _activeTriggers = [];

    public void SetTarget(WindowInfo? target)
    {
        _target = target;
        Refresh();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Refresh();
    }

    /// <summary>Hides the outlines while true (the box editor is open).</summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        Refresh();
    }

    public void Refresh()
    {
        if (_closed)
            return;
        var profile = activeProfile();
        // The pipeline sends no "lost" for a removed zone or a switched profile, so forget those here.
        if (profile.Name != _profileName)
        {
            _profileName = profile.Name;
            _activeTriggers.Clear();
        }
        _activeTriggers.RemoveWhere(id => profile.Zones.All(z => z.Id != id));

        var zones = new List<ZoneFrames>();
        foreach (var z in profile.Zones)
        {
            // The caption box is only where the Thai text goes in the Custom placement.
            var caption = z.Placement == OverlayPlacement.Custom ? z.GetCaptionRegion() : null;
            var trigger = z.TriggerEnabled ? z.TriggerRegion : null;
            if (z.Region is null && caption is null && trigger is null)
                continue;
            zones.Add(new ZoneFrames(z.Name, z.Region, caption, trigger, _activeTriggers.Contains(z.Id)));
        }
        if (!_enabled || _suppressed || zones.Count == 0
            || _target is not { IsMinimized: false } target
            || target.ClientScreenRect.Width <= 0 || target.ClientScreenRect.Height <= 0)
        {
            if (window.IsVisible)
                window.Hide();
            return;
        }
        window.ShowFrames(target.ClientScreenRect, zones);
    }

    /// <summary>A zone's trigger word was seen (true) or lost (false).</summary>
    public void SetTriggerActive(string zoneId, bool active)
    {
        if (active ? _activeTriggers.Add(zoneId) : _activeTriggers.Remove(zoneId))
            Refresh();
    }

    public void Close()
    {
        _closed = true;
        window.Close();
    }
}
