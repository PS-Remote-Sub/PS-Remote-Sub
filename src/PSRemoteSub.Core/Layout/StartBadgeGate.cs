namespace PSRemoteSub.Core.Layout;

/// <summary>
/// Decides when the "Thai subtitles are on" start badge shows: whenever translation starts. That is the first time the game
/// window is found and reading runs after the app starts, resuming from pause, and turning the overlay back on. The game
/// window coming back later (minimised, restored) does not show it again by itself. Pure; UI thread only.
/// </summary>
public sealed class StartBadgeGate
{
    private bool _found, _paused, _enabled = true, _shown;

    /// <summary>Whether translation is running now: the window found, not paused, overlay on.</summary>
    public bool Active => _found && !_paused && _enabled;

    /// <summary>Gives the current state; true when the badge should (re)start now.</summary>
    public bool Update(bool targetFound, bool paused, bool overlayEnabled)
    {
        bool wasActive = Active;
        bool foundChanged = targetFound != _found;
        _found = targetFound;
        _paused = paused;
        _enabled = overlayEnabled;
        if (!Active || wasActive)
            return false;
        // Only the window coming back: not a new start, unless the badge never showed yet.
        if (foundChanged && _shown)
            return false;
        _shown = true;
        return true;
    }
}
