using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Capture;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App;

/// <summary>Maps <see cref="PipelineStatus"/> to the Thai status copy.</summary>
/// <remarks>
/// <see cref="PipelineStatus.RemotePlayFound"/> means "the picture source's target was found": the Remote Play window,
/// or the capture card window's video while the card plays.
/// </remarks>
internal static class StatusCopy
{
    /// <summary>
    /// The most relevant status line (tray tooltip, settings window), plus the AI error on a second line when reading
    /// is not paused and the error is still news: no retry time, or one still ahead. A pushed status can outlive its
    /// backoff, so that is checked here.
    /// </summary>
    /// <param name="source">The picture source in use.</param>
    /// <param name="capture">
    /// With <see cref="PictureSource.CaptureCard"/>: the card's state while the capture window is open, or null while it
    /// is closed. Ignored for Remote Play.
    /// </param>
    public static string Describe(PipelineStatus s, bool paused, DateTimeOffset now, PictureSource source,
        CaptureState? capture = null, string selectRegionHotkey = "")
    {
        if (paused || s.Paused) return Loc.Get("Paused");
        string line = !s.RemotePlayFound ? Loc.Get(NotFoundKey(source, capture))
            : ErrorKey(s, source, selectRegionHotkey) is { } key ? key
            : Loc.Get("Reading");
        bool showAi = s.AiError is not null && (s.AiRetryAt is not { } retry || retry > now);
        return showAi ? line + "\n" + Loc.Get(s.AiError!) : line;
    }

    /// <summary>
    /// The error the overlay may show while no subtitle is on screen, or null. "Not found" is not included: the
    /// overlay is hidden without a window. Paused is a choice, not an error. The AI error is never shown here: the
    /// line still appears (as the source, marked untranslated), and the tray tooltip carries the error.
    /// </summary>
    public static string? OverlayError(PipelineStatus s, bool paused, PictureSource source, string selectRegionHotkey = "")
    {
        if (paused || s.Paused || !s.RemotePlayFound) return null;
        return ErrorKey(s, source, selectRegionHotkey);
    }

    /// <summary>The key for "no target": Remote Play not found, the capture window closed, or the card's state.</summary>
    private static string NotFoundKey(PictureSource source, CaptureState? capture)
    {
        if (source != PictureSource.CaptureCard) return "NoRemotePlay";
        // Playing without a target yet: the probe has not caught up (or the video is not laid out yet).
        return capture is { } state ? CaptureText.MessageKey(state) : "CaptureWindowClosed";
    }

    /// <summary>The localised error text, or null. <paramref name="selectRegionHotkey"/> is the shown (per-OS) region hotkey.</summary>
    private static string? ErrorKey(PipelineStatus s, PictureSource source, string selectRegionHotkey)
    {
        bool card = source == PictureSource.CaptureCard;
        if (s.Minimized) return Loc.Get(card ? "CaptureMinimized" : "Minimized");
        if (s.NoRegion) return Loc.Format("NoRegion", selectRegionHotkey);
        if (s.OcrUnavailable) return Loc.Get("OcrUnavailable");
        if (s.FramesStalled) return Loc.Get(card ? "CaptureFramesStalled" : "FramesStalled");
        return null;
    }
}
