using Avalonia.Threading;
using PSRemoteSub.App.Views;
using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.App;

/// <summary>
/// Decides what the overlay shows and where. UI thread only: <see cref="AppHost"/> marshals hub and watcher
/// events here with <see cref="Dispatcher.UIThread"/>.
/// </summary>
internal sealed class OverlayController(OverlayWindow window, Func<GameProfile> activeProfile,
    Func<PictureSource> pictureSource, string zoneId, bool showsStatus, Action<string, TvZoneView?> tvSink,
    Func<string> selectRegionHotkey)
{
    /// <summary>The status line shows in one zone's box only (the first); <see cref="ZoneOverlays"/> moves it when zones change.</summary>
    public bool ShowsStatus { get; set; } = showsStatus;

    public string ZoneId => zoneId;

    /// <summary>Where a status line goes when no region has been selected yet: a band near the bottom.</summary>
    private static readonly RegionFraction StatusRegion = new(0.1, 0.8, 0.8, 0.12);

    private WindowInfo? _target;
    // The shown line (none: an error status may show instead) and the lines waiting for their turn.
    private readonly OverlayLines _lines = new();
    // Lines published without text yet (the AI has not answered), and answers waiting for an older line's.
    private readonly AnswerOrder _answers = new();
    // Fires when the shown line's minimum time is up (next line, or hide after the game cleared).
    private DispatcherTimer? _timer;
    private PipelineStatus _status = PipelineStatus.Initial;
    private bool _paused;
    private bool _enabled = true;
    private bool _suppressed;
    private bool _closed;

    public void SetTarget(WindowInfo? target)
    {
        // A different (or no) Remote Play window: the old subtitle no longer belongs on screen.
        if (target?.Handle != _target?.Handle)
            ForgetSubtitle();
        _target = target;
        Refresh();
    }

    public void ShowSubtitle(SubtitleEntry entry)
    {
        // A translation that finishes after the user paused belongs to the reading they stopped: don't show it.
        if (_paused)
            return;
        var now = DateTimeOffset.UtcNow;
        ApplyMinDisplay();
        _lines.SetGameLine(entry.Id, now);
        if (IsPending(entry))
            _answers.Expect(entry.Id); // no text yet: what is on screen stays until the AI answers (or gives up)
        else
            _lines.Show(entry.Id, TextOf(entry), entry.AwaitingAi, now, entry.Bounds, entry.Speakers, entry.RowBounds);
        Update();
    }

    /// <summary>
    /// The AI lane refined (or settled) <paramref name="entry"/>: replaces the text of the line with that id, shown or
    /// waiting. A pending line (no text yet) now takes its turn, even if the game cleared it meanwhile, so every line can
    /// be read; one older than a line already shown is dropped. Answers take their turns in the order of their lines:
    /// one that comes before an older pending line's waits for it. A pause wins.
    /// </summary>
    public void RefineSubtitle(SubtitleEntry entry)
    {
        if (_paused)
            return;
        if (_answers.Holds(entry.Id))
        {
            if (IsPending(entry))
                return; // still no text: keep waiting
            var due = _answers.Answer(entry);
            foreach (var answer in due)
            {
                ApplyMinDisplay();
                _lines.Show(answer.Id, TextOf(answer), answer.AwaitingAi, DateTimeOffset.UtcNow, answer.Bounds, answer.Speakers,
                    answer.RowBounds);
            }
            if (due.Count > 0)
                Update();
            return;
        }
        if (_lines.Refine(entry.Id, TextOf(entry), entry.AwaitingAi))
            Refresh();
    }

    /// <summary>The game subtitle went away: the shown line hides once its minimum time is up (after any waiting lines).</summary>
    public void ClearSubtitle()
    {
        ApplyMinDisplay();
        _lines.SetGameLine(null, DateTimeOffset.UtcNow);
        Update();
    }

    /// <summary>The zone is hidden at once: its shown and waiting lines and pending answers go, so a late answer never shows.</summary>
    public void HideZone()
    {
        ForgetSubtitle();
        Refresh();
    }

    /// <summary>How many lines may wait for their turn on the overlay (the AI's answers can come several at once).</summary>
    public void SetMaxWaiting(int lines) => _lines.MaxWaiting = lines;

    public void SetStatus(PipelineStatus status)
    {
        _status = status;
        Refresh();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused)
            ForgetSubtitle(); // stale while nothing is being read
        Refresh();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Refresh();
    }

    /// <summary>Hides the overlay while true, whatever else is set (the region picker is open).</summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        Refresh();
    }

    /// <summary>Shows the subtitle, or an error status when there is no subtitle, or hides the overlay.</summary>
    public void Refresh()
    {
        if (_closed)
            return;
        var profile = activeProfile();
        var zone = profile.GetZone(zoneId);
        var current = _lines.Current;
        // The TV gets the shown subtitle (never a status line) whatever the PC overlay does: sent before the _enabled and
        // _suppressed gates below. Pausing hides it too (SetPaused already cleared the lines; the check covers a late call).
        TvZoneView? tvView = null;
        if (zone is not null && _target is { IsMinimized: false } tvTarget)
            tvView = TvViews.From(zone, _paused ? null : current, profile.FontSize,
                tvTarget.ClientScreenRect.Width, tvTarget.ClientScreenRect.Height);
        tvSink(zoneId, tvView);
        string? text = current?.Text ?? (ShowsStatus ? StatusCopy.OverlayError(_status, _paused, pictureSource(), selectRegionHotkey()) : null);
        // Where the line is drawn: the caption box (the OCR box until one is set); a status line with neither goes in a
        // band near the bottom.
        var captionBox = zone?.GetCaptionRegion() ?? (current is null && ShowsStatus ? StatusRegion : null);
        if (zone is null || !_enabled || _suppressed || text is null || captionBox is null
            || _target is not { IsMinimized: false } target
            || target.ClientScreenRect.Width <= 0 || target.ClientScreenRect.Height <= 0)
        {
            if (window.IsVisible)
                window.Hide();
            return;
        }
        // Before ShowText, so the line never flashes at the wrong opacity. A status line is never provisional. Only the
        // latest subtitle is shown (the user asked for one at a time).
        window.SetProvisional(current is { AwaitingAi: true });
        // A status line has no bounds: it goes in the caption box.
        window.ShowText(text, profile, zone.Placement, target.ClientScreenRect, zone.Region, captionBox, current?.Bounds, current?.Speakers, current?.RowBounds);
    }

    public void Close()
    {
        _closed = true;
        _timer?.Stop();
        tvSink(zoneId, null);
        window.Close();
    }

    private void ForgetSubtitle()
    {
        _timer?.Stop();
        _lines.Reset();
        _answers.Reset();
    }

    /// <summary>The active profile's minimum time, read on every change so an edit in Settings applies to the next line.</summary>
    private void ApplyMinDisplay() =>
        _lines.MinDisplay = TimeSpan.FromSeconds(Math.Clamp(activeProfile().MinDisplaySeconds, 0, GameProfile.MinDisplayMax));

    /// <summary>Lets due lines move on, shows the result and arms the timer for the next change.</summary>
    private void Update()
    {
        if (_closed)
            return;
        var now = DateTimeOffset.UtcNow;
        ApplyMinDisplay();
        _lines.Advance(now);
        Refresh();

        _timer?.Stop();
        if (_paused || _lines.NextChangeIn(now) is not { } wait)
            return;
        if (_timer is null)
        {
            _timer = new DispatcherTimer();
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                // Not posted through AppHost, so nothing else catches here: a failure must not end the app mid-game.
                try
                {
                    if (!_paused)
                        Update();
                }
                catch (Exception ex)
                {
                    Log.Warn("Overlay timer update failed", ex); // the next event redraws from current state
                }
            };
        }
        _timer.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    /// <summary>Before the AI answered: there is nothing to show yet.</summary>
    private static bool IsPending(SubtitleEntry entry) => entry is { AwaitingAi: true, Translated: null };

    private static string TextOf(SubtitleEntry entry) =>
        entry.Translated ?? $"{entry.Dialogue ?? entry.Source} {Loc.Get("UntranslatedSuffix")}"; // names come from Speakers
}
