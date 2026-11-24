namespace PSRemoteSub.Core.Capture;

/// <summary>Pure decisions the capture window makes about recovering the card and keeping the display on.</summary>
public static class CaptureWindowRules
{
    /// <summary>
    /// While the window is open: whether to list the devices again and open the one to use. Only while the card is missing
    /// (none found, or unplugged: it may come back with a new id on another USB port).
    /// </summary>
    public static bool ShouldLookForDevice(CaptureState state) =>
        state is CaptureState.NoDevice or CaptureState.Unplugged;

    /// <summary>
    /// On a state change: whether to look up the card's audio again and start it. When the picture comes back (an
    /// unplug, a sleep) with the audio not playing and no start of it still pending.
    /// </summary>
    public static bool ShouldRestartAudio(CaptureState previous, CaptureState current, bool audioRunning, bool audioStartPending) =>
        current == CaptureState.Playing && previous != CaptureState.Playing && !audioRunning && !audioStartPending;

    /// <summary>
    /// Whether the capture window needs the display kept on: someone is playing through it (open, not minimised, the card
    /// playing), whether or not subtitles are being read. Subject to the keep-awake setting.
    /// </summary>
    public static bool KeepsDisplayAwake(bool windowOpen, bool windowMinimized, CaptureState? state) =>
        windowOpen && !windowMinimized && state == CaptureState.Playing;
}
