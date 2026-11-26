using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;
using PSRemoteSub.Core.Layout;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Platform;
using PSRemoteSub.Core.Settings;
using PSRemoteSub.Core.Text;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// Reads every OCR zone's box every <see cref="PipelineOptions.OcrInterval"/> (one after another, in list order; a zone
/// with trigger words only while they are seen), waits for each zone's text to settle, publishes it to the
/// <see cref="SubtitleHub"/> as pending and hands it to the <see cref="AiRefiner"/>, which translates it (a queue in
/// order, one request in flight) or settles it as untranslated. A line the game's translation library already has is
/// published translated at once instead, and only kept as AI context. In Bubbles mode this goes per bubble, and a zone
/// also remembers the bubbles translated so far: only the bubbles not known yet go to the AI.
/// </summary>
/// <remarks>
/// Threading:
/// <list type="bullet">
/// <item>Ticks run one at a time (<see cref="RunAsync"/> awaits each; a re-entrant <see cref="TickAsync"/> throws).
/// The frame source, OCR engine, per-zone state (stability filter, trigger gate, last reads) and all capture
/// bookkeeping are touched only by the tick.</item>
/// <item><see cref="IWindowWatcher.Changed"/> arrives on the watcher's thread (sometimes inside a native callback).
/// The handler only stores the reported window in a field; the next tick compares handles and calls
/// <see cref="IFrameSource.Stop"/>/<see cref="IFrameSource.Start"/>. So the frame source is never driven from the
/// watcher thread nor concurrently with a tick, and the handler cannot throw. Cost: up to one interval of latency.</item>
/// <item><see cref="UpdateConfig"/> may be called from any thread. It swaps an immutable config under a small lock;
/// a change to a zone's reading bumps that zone's epoch (a source change bumps all), which the tick uses to reset the
/// zone's filter and to drop an OCR result read under the old config.</item>
/// <item>A stable line is published on the tick (so a clear that follows can never overtake it) and handed to the
/// <see cref="AiRefiner"/>, which runs its own queue lane off the tick and fills the entry in place. A late
/// answer only refines its entry; the overlay and the web page keep a line the game already cleared hidden.</item>
/// <item>New speaker names are looked up with the AI alongside, off the tick; the lines that follow pick them up.</item>
/// <item>Status has two writers (the tick and the AI lane via <see cref="UpdateAiStatus"/>). Both merge into one
/// record under a lock and call <see cref="SubtitleHub.SetStatus"/> while holding it, so updates cannot be applied
/// out of order. Hub event handlers for <see cref="StatusChanged"/> therefore run under that lock and must not wait on
/// the pipeline.</item>
/// </list>
/// </remarks>
public sealed class SubtitlePipeline : IDisposable
{
    private readonly IWindowWatcher _watcher;
    private readonly IFrameSource _frames;
    private readonly IOcrEngine _ocr;
    private readonly SubtitleHub _hub;
    private readonly TimeProvider _time;
    private readonly PipelineOptions _options;
    private readonly AiRefiner _aiRefiner;
    private readonly Action<string, string, SpeakerInfo>? _onSpeakerLearned;
    private readonly LibraryHolder? _library;
    // Speaker names of the current profile; replaced when the profile changes.
    private SpeakerBook? _book;

    // Latest window reported by the watcher. Written on the watcher thread, read by the tick.
    private WindowSnapshot? _window;

    // Written by UpdateConfig (any thread), read by the tick.
    private readonly Lock _configGate = new();
    private PipelineConfig? _config;
    // One entry per zone id of _config; replaced as a whole by every UpdateConfig.
    private IReadOnlyDictionary<string, ZoneEpoch> _epochs = new Dictionary<string, ZoneEpoch>();
    private int _epochCounter;
    // Bumped whenever the AI context resets (see ZoneEpoch.Context).
    private int _contextEpoch;

    // Tick-only state.
    private int _ticking;
    private nint? _captureHandle;
    private bool _captureStarted;
    private bool _reading;
    // Frames older than this do not count towards a stall: capture (re)started or reading resumed here.
    private DateTimeOffset _stallClockFrom;
    // Per zone id, for the zones read so far; a zone that leaves the config is hidden and dropped.
    private readonly Dictionary<string, ZoneState> _zones = [];
    private readonly LogThrottle _tickFaults = new();
    private readonly LogThrottle _captureFaults = new();
    private readonly LogThrottle _ocrFaults = new();
    private readonly LogThrottle _slowTicks = new();

    // Written by the tick, read by LastCapture readers.
    private Capture? _lastCapture;

    private readonly Lock _statusGate = new();
    private PipelineStatus _status = PipelineStatus.Initial;

    /// <param name="aiRefiner">
    /// The AI lane, which translates every line. Not owned: the caller disposes it after this pipeline.
    /// <para>Construction order: the refiner reports status through <see cref="UpdateAiStatus"/> of the pipeline
    /// that is built from it, so give it a late-bound delegate and assign the pipeline right after:</para>
    /// <code>
    /// SubtitlePipeline? pipeline = null;
    /// var refiner = new AiRefiner(hub, aiSource, time, change => pipeline?.UpdateAiStatus(change));
    /// pipeline = new SubtitlePipeline(watcher, frames, ocr, hub, time, options, refiner);
    /// </code>
    /// The refiner only reports status from <see cref="AiRefiner.OnPublished"/> and its own lane, both of which start
    /// after the pipeline exists, so the null branch is never taken in practice.
    /// </param>
    /// <param name="onSpeakerLearned">
    /// Called (on a pipeline thread) when a speaker name gets its Thai spelling or colour: profile name, source name,
    /// what is now known. The caller saves it to that profile.
    /// </param>
    /// <param name="library">
    /// The game's translation library, read on every line (the tick looks lines up in it; lookups are fast). Give the
    /// refiner the same holder.
    /// </param>
    public SubtitlePipeline(IWindowWatcher watcher, IFrameSource frames, IOcrEngine ocr, SubtitleHub hub,
        TimeProvider time, PipelineOptions options, AiRefiner aiRefiner,
        Action<string, string, SpeakerInfo>? onSpeakerLearned = null, LibraryHolder? library = null)
    {
        _onSpeakerLearned = onSpeakerLearned;
        _library = library;
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(ocr);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(aiRefiner);
        if (options.OcrInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "OcrInterval must be positive.");

        _watcher = watcher;
        _frames = frames;
        _ocr = ocr;
        _hub = hub;
        _time = time;
        _options = options;
        _aiRefiner = aiRefiner;

        _watcher.Changed += OnWindowChanged;
        // Subscribe first, then seed from Current unless the handler already stored something newer.
        Interlocked.CompareExchange(ref _window, new WindowSnapshot(watcher.Current), null);
    }

    /// <summary>
    /// The last OCR box crop fed to OCR (of the last zone read), before and after preprocessing (for the save-frame
    /// hotkey). Trigger boxes are not kept.
    /// </summary>
    public (Frame Raw, Frame Processed)? LastCapture =>
        Volatile.Read(ref _lastCapture) is { } c ? (c.Raw, c.Processed) : null;

    /// <summary>
    /// Thread-safe. A zone whose reading changed (<see cref="ZoneConfig.SameReading"/>) reads again, and its stability
    /// filter starts again unless only its filter changed (it then hides on its next tick, and an active trigger goes
    /// off); a source language change does both for every zone. Other
    /// zones keep their state. The AI context resets when the source language, game title, target language or profile
    /// changes, and so do the bubbles every zone remembers.
    /// </summary>
    public void UpdateConfig(PipelineConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(config.Zones);
        bool resetAiContext = false;
        lock (_configGate)
        {
            if (_config is { } old)
            {
                resetAiContext = old.Source != config.Source || old.GameTitle != config.GameTitle
                    || old.TargetLang != config.TargetLang || old.ProfileName != config.ProfileName;
            }
            if (resetAiContext)
                _contextEpoch++;
            _epochs = NextEpochs(_config, config);
            // Another profile has its own names. The same profile keeps the book: what it learned is already saved.
            if (_config is null || _config.ProfileName != config.ProfileName)
                Volatile.Write(ref _book, new SpeakerBook(config.Speakers, _time));
            _config = config;
        }
        if (resetAiContext)
            _aiRefiner.ResetContext();
    }

    /// <summary>The zone epochs for <paramref name="next"/>. Under <see cref="_configGate"/>.</summary>
    private Dictionary<string, ZoneEpoch> NextEpochs(PipelineConfig? old, PipelineConfig next)
    {
        bool sourceChanged = old is not null && old.Source != next.Source;
        var epochs = new Dictionary<string, ZoneEpoch>();
        foreach (var zone in next.Zones)
        {
            if (epochs.ContainsKey(zone.Id))
                continue; // a duplicate id: the first zone wins (the tick reads it only)
            var before = old?.Zones.FirstOrDefault(z => z.Id == zone.Id);
            if (before is null || !_epochs.TryGetValue(zone.Id, out var epoch))
            {
                int fresh = ++_epochCounter;
                epoch = new ZoneEpoch(fresh, fresh, _contextEpoch);
            }
            else if (sourceChanged || !before.SameReading(zone))
            {
                int changed = ++_epochCounter;
                // Only the image filter changed: the same text is on screen, so what the filter saw so far stands.
                bool filterOnly = !sourceChanged && before.SameReading(zone with { Filter = before.Filter });
                epoch = new ZoneEpoch(changed, filterOnly ? epoch.Reset : changed, _contextEpoch);
            }
            epochs[zone.Id] = epoch with { Context = _contextEpoch };
        }
        return epochs;
    }

    /// <summary>Thread-safe. Merges an AI lane change (<see cref="PipelineStatus.AiError"/>/<see cref="PipelineStatus.AiRetryAt"/>) into the status.</summary>
    public void UpdateAiStatus(Func<PipelineStatus, PipelineStatus> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        UpdateStatus(change);
    }

    /// <summary>Runs <see cref="TickAsync"/> every <see cref="PipelineOptions.OcrInterval"/> until cancelled.</summary>
    /// <exception cref="OperationCanceledException">When <paramref name="ct"/> is cancelled.</exception>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.OcrInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A fault in one tick (for example a hub subscriber that threw) must not stop the loop.
                if (_tickFaults.ShouldLog(out int count))
                    Log.Error($"Pipeline tick failed (#{count})", ex);
            }
        }
    }

    /// <summary>One read cycle. Public for tests; must not be called concurrently with itself or <see cref="RunAsync"/>.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            throw new InvalidOperationException("TickAsync must not run concurrently.");
        try
        {
            await TickCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    public void Dispose() => _watcher.Changed -= OnWindowChanged;

    private void OnWindowChanged(object? sender, WindowInfo? window) =>
        Volatile.Write(ref _window, new WindowSnapshot(window));

    private async Task TickCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        var window = SyncCapture(now);

        PipelineConfig? config;
        IReadOnlyDictionary<string, ZoneEpoch> epochs;
        lock (_configGate)
        {
            config = _config;
            epochs = _epochs;
        }
        DropRemovedZones(epochs);

        bool found = window is not null;
        bool paused = config?.Paused ?? false;
        bool minimized = window?.IsMinimized ?? false;
        bool noRegion = config is null || !config.Zones.Any(z => z.Region is not null);
        if (config is null || noRegion || paused || window is null || minimized)
        {
            _reading = false;
            SetTickStatus(found, stalled: false, paused, noRegion, minimized, ocrUnavailable: false);
            return;
        }
        if (!IsOcrAvailable(config.Source.OcrTag))
        {
            _reading = false;
            SetTickStatus(found, stalled: false, paused: false, noRegion: false, minimized: false, ocrUnavailable: true);
            return;
        }
        if (!_reading)
        {
            // Reading (re)starts (restored, resumed, region chosen, OCR installed): the frame on hand may be old
            // only because nothing was asked of it, so the stall clock starts again from now.
            _reading = true;
            _stallClockFrom = now;
        }

        // WGC delivers a frame only when the window content changes, so an old frame on a still scene is normal.
        // A stall means no frame at all for StaleFrameAfter (a backstop for a capture that died).
        var frame = _frames.Latest;
        var newest = _frames.LastFrameAt is { } at && at > _stallClockFrom ? at : _stallClockFrom;
        bool stalled = now - newest >= _options.StaleFrameAfter;
        SetTickStatus(found, stalled, paused: false, noRegion: false, minimized: false, ocrUnavailable: false);
        if (stalled || frame is null)
            return;

        long started = _time.GetTimestamp();
        int zonesRead = 0;
        var seen = new HashSet<string>();
        foreach (var zone in config.Zones)
        {
            if (zone.Region is not { } region || !seen.Add(zone.Id) || !epochs.TryGetValue(zone.Id, out var epoch))
                continue;
            zonesRead++;
            await ReadZoneAsync(config, zone, region, epoch, frame, now, ct).ConfigureAwait(false);
        }
        var took = _time.GetElapsedTime(started);
        if (took > _options.OcrInterval && _slowTicks.ShouldLog(out _))
            Log.Warn($"Tick took {(long)took.TotalMilliseconds} ms for {zonesRead} zones");
    }

    /// <summary>Hides and forgets the zones that left the config. Tick only.</summary>
    private void DropRemovedZones(IReadOnlyDictionary<string, ZoneEpoch> epochs)
    {
        foreach (var id in _zones.Keys.Where(id => !epochs.ContainsKey(id)).ToList())
        {
            _zones.Remove(id);
            _hub.HideZone(id);
        }
    }

    /// <summary>
    /// One zone's turn in a tick: its trigger check, then the same read as before zones (crop, preprocess, OCR, split,
    /// bounds), its own stability filter, and publish or clear for that zone. Tick only.
    /// </summary>
    private async Task ReadZoneAsync(PipelineConfig config, ZoneConfig zone, RegionFraction region, ZoneEpoch epoch,
        Frame frame, DateTimeOffset now, CancellationToken ct)
    {
        if (!_zones.TryGetValue(zone.Id, out var state))
            _zones[zone.Id] = state = new ZoneState(_options.Stability, epoch);
        if (state.Apply(epoch))
        {
            // Its filter started again, so it would never clear what it showed: hide it now, and switch an active
            // trigger off (the new reading decides again whether it is on).
            if (state.Gate.Active)
            {
                state.Gate.Reset();
                _hub.SetTrigger(zone.Id, false);
            }
            _hub.HideZone(zone.Id);
        }

        if (!await IsTriggeredAsync(config, zone, state, frame, ct).ConfigureAwait(false))
            return;

        LastRead read;
        if (state.LastRead is { } last && ReferenceEquals(last.Frame, frame))
        {
            // Same frame as last tick: the screen has not changed, so its text has not either. Feeding the earlier
            // read again (without OCR) lets a still subtitle settle and a vanished one clear on schedule. A change to
            // the zone's reading or the source drops the earlier read (ZoneState.Apply).
            read = last;
        }
        else
        {
            if (await RecognizeAsync(zone, state, region, frame, config.Source, keepCapture: true, ct).ConfigureAwait(false)
                is not { } box)
                return;
            read = Interpret(box, zone, config.Source, frame);
            state.LastRead = read;
        }

        HideIfMoving(zone, state, read.Text);
        switch (state.Filter.Feed(read.Text, now))
        {
            case StabilityDecision.Stable stable:
                state.Shown = stable.Text;
                state.HiddenWhileMoving = false;
                var reads = read.Reads;
                // Names count, and are coloured, only from stable subtitles: never from a fade-in frame or an OCR slip.
                if (Volatile.Read(ref _book) is { } book && reads.Any(r => r.Name is not null))
                {
                    book.Sight(reads.Where(r => r.Name is not null).Select(r => r.Name!));
                    LearnNameColors(reads, read.Raw, read.OcrScale, config.ProfileName);
                }
                // The bounds and speakers of this read: the one that made the text stable, so they match it.
                var line = new StableLine(stable.Text, config.Source, config.TargetLang, config.GameTitle,
                    config.ContextLines, ct, read.Bounds, read.RowBounds, reads, config.ProfileName, zone.Id, zone.Name,
                    zone.SendToWeb, zone.LearnToLibrary, config.SpeakerColors);
                // New speaker names (once per name). An AI call can take seconds: it runs alongside, and the lines
                // that follow pick the names up.
                if (line.HasNames)
                    TrackNameLookup(LearnNamesAsync(line));
                Publish(line, zone.Placement == OverlayPlacement.Fit ? state.Memory : null);
                break;
            case StabilityDecision.Cleared:
                state.Shown = null;
                _hub.Clear(zone.Id);
                break;
        }
    }

    /// <summary>
    /// True when the zone reads this tick: it has no trigger, or its trigger words are seen (with a short grace after
    /// they go, see <see cref="TriggerGate"/>). An enabled trigger without a box or words never reads. Tick only.
    /// </summary>
    private async Task<bool> IsTriggeredAsync(PipelineConfig config, ZoneConfig zone, ZoneState state, Frame frame,
        CancellationToken ct)
    {
        // A change to the trigger (switched off, box or words removed) already switched the gate off and hid the zone
        // in ZoneState.Apply, so these two need nothing more.
        if (zone.Trigger is not { } trigger)
            return true;
        if (trigger.Region is not { } box || trigger.Words.Count == 0)
            return false;

        bool matched;
        if (ReferenceEquals(state.TriggerFrame, frame))
        {
            matched = state.TriggerMatched; // same frame as last tick: the same words are on screen
        }
        else
        {
            if (await RecognizeAsync(zone, state, box, frame, config.Source, keepCapture: false, ct).ConfigureAwait(false)
                is not { } read)
                return false;
            matched = TriggerWords.Matches(read.Lines, trigger.Words);
            state.TriggerFrame = frame;
            state.TriggerMatched = matched;
        }

        switch (state.Gate.Feed(matched))
        {
            case TriggerChange.On:
                _hub.SetTrigger(zone.Id, true);
                break;
            case TriggerChange.Off:
                TurnOff(zone, state);
                break;
        }
        return state.Gate.Active;
    }

    /// <summary>The zone's trigger goes off: its subtitle hides at once and its filter starts again. Tick only.</summary>
    private void TurnOff(ZoneConfig zone, ZoneState state)
    {
        state.Gate.Reset();
        state.ResetFilter();
        _hub.SetTrigger(zone.Id, false);
        _hub.HideZone(zone.Id);
    }

    /// <summary>
    /// Bubbles zones with a trigger: once the read moves away from the shown text (scrolled, changed or gone), the zone
    /// hides at once so its boxes never sit over the wrong bubbles, and its filter starts again so the same text can
    /// show again when it settles. Once per shown line; OCR noise (still a duplicate of the shown text) does not count.
    /// A Bubbles zone without a trigger (such as a dialogue zone saved in Bubbles mode before zones) behaves as before
    /// zones: its line keeps the minimum display time and its pending answer still lands. Tick only.
    /// </summary>
    private void HideIfMoving(ZoneConfig zone, ZoneState state, string? text)
    {
        if (zone.Placement != OverlayPlacement.Fit || zone.Trigger is null || state.Shown is not { } shown
            || state.HiddenWhileMoving)
            return;
        if (text is not null && Similarity.Ratio(text, shown) >= _options.Stability.DuplicateThreshold)
            return;
        state.HiddenWhileMoving = true;
        state.Filter.Reset();
        _hub.HideZone(zone.Id);
    }

    /// <summary>
    /// Crops <paramref name="box"/>, applies the zone's filter and runs OCR. Null when it fails (logged, throttled per
    /// zone) or when the zone's reading changed while OCR ran: that text belongs to the old config. Tick only.
    /// </summary>
    private async Task<BoxRead?> RecognizeAsync(ZoneConfig zone, ZoneState state, RegionFraction box, Frame frame,
        SourceLanguage source, bool keepCapture, CancellationToken ct)
    {
        BoxRead read;
        try
        {
            var boxPx = box.ToPixels(frame.Width, frame.Height);
            var raw = frame.Crop(boxPx);
            var processed = Preprocessor.Apply(raw, zone.Filter, _ocr.MaxImageDimension);
            if (keepCapture)
                Volatile.Write(ref _lastCapture, new Capture(raw, processed));
            var lines = await _ocr.RecognizeAsync(processed, source.OcrTag, ct).ConfigureAwait(false);
            read = new BoxRead(lines, raw, (double)processed.Width / raw.Width, boxPx);
            state.OcrFaults.Reset();
            _ocrFaults.Reset();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (state.OcrFaults.ShouldLog(out int count))
                Log.Warn($"OCR failed (#{count})", ex);
            return null; // OCR or frame fault: skip this zone for this tick.
        }

        lock (_configGate)
        {
            if (!_epochs.TryGetValue(zone.Id, out var current) || current.Read != state.Epoch.Read)
                return null;
        }
        return read;
    }

    /// <summary>Splits an OCR read into speaker lines (or bubbles) and finds where they are on screen.</summary>
    private static LastRead Interpret(BoxRead box, ZoneConfig zone, SourceLanguage source, Frame frame)
    {
        // One line per speaker (a name label or a dash starts one), and in Bubbles mode one per bubble; without
        // either this is the joined text.
        var split = zone.Placement == OverlayPlacement.Fit
            ? BubbleSplitter.Split(box.Lines, source)
            : SpeakerSplitter.Split(box.Lines, source);
        var reads = SpeakerSplitter.SpeakerLinesOnly(split, zone.SpeakerLinesOnly);
        var joined = SpeakerSplitter.Compose(reads);
        if (!TextNormalizer.HasSubtitle(joined, source))
            return new LastRead(frame, null, null, null, [], box.Raw, box.OcrScale);
        var bounds = SubtitleBounds.FromOcr(box.Lines, box.OcrScale, box.BoxPx, frame.Width, frame.Height);
        // Each speaker's own lines, so the overlay can cover them with one box per row.
        var rowBounds = SubtitleBounds.ForRows(reads, box.Lines, box.OcrScale, box.BoxPx, frame.Width, frame.Height);
        return new LastRead(frame, joined, bounds, rowBounds, reads, box.Raw, box.OcrScale);
    }

    /// <summary>Applies the latest watcher report to the frame source. Tick only.</summary>
    private WindowInfo? SyncCapture(DateTimeOffset now)
    {
        var window = Volatile.Read(ref _window)?.Window;
        nint? handle = window?.Handle;
        if (handle != _captureHandle)
        {
            _captureHandle = handle;
            _captureStarted = false;
            _stallClockFrom = now;
            _captureFaults.Reset();
            Log.Info(window is null ? "Remote Play window lost" : "Remote Play window found");
            if (window is null)
                TryFrames("stop", _frames.Stop);
        }
        // A failed start is retried on every tick until it succeeds or the window changes.
        if (window is not null && !_captureStarted)
        {
            _captureStarted = TryFrames("start", () =>
            {
                _frames.Stop();
                _frames.Start(window);
            });
            if (_captureStarted)
            {
                _captureFaults.Reset();
                Log.Info("Capture started");
            }
        }
        return window;
    }

    private bool TryFrames(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            if (_captureFaults.ShouldLog(out int count))
                Log.Warn($"Capture {what} failed (#{count})", ex);
            return false;
        }
    }

    private bool IsOcrAvailable(string ocrTag)
    {
        try
        {
            return _ocr.IsAvailable(ocrTag);
        }
        catch (Exception ex)
        {
            if (_ocrFaults.ShouldLog(out int count))
                Log.Warn($"OCR availability check failed (#{count})", ex);
            return false;
        }
    }

    private void SetTickStatus(bool found, bool stalled, bool paused, bool noRegion, bool minimized, bool ocrUnavailable) =>
        UpdateStatus(s => s with
        {
            RemotePlayFound = found,
            FramesStalled = stalled,
            Paused = paused,
            NoRegion = noRegion,
            Minimized = minimized,
            OcrUnavailable = ocrUnavailable,
        });

    private void UpdateStatus(Func<PipelineStatus, PipelineStatus> change)
    {
        lock (_statusGate)
        {
            var old = _status;
            _status = change(_status);
            if (StatusFlags(_status) != StatusFlags(old))
                Log.Info("Status: " + StatusFlags(_status));
            _hub.SetStatus(_status);
        }
    }

    private static string StatusFlags(PipelineStatus s) =>
        $"found={B(s.RemotePlayFound)} minimized={B(s.Minimized)} paused={B(s.Paused)} noRegion={B(s.NoRegion)} " +
        $"ocrUnavailable={B(s.OcrUnavailable)} stalled={B(s.FramesStalled)} ai={s.AiError ?? "ok"}";

    private static char B(bool value) => value ? '1' : '0';

    /// <summary>
    /// Publishes a stable line. When the library has every part of it, the line is shown translated at once and the
    /// AI lane only keeps it as context. Otherwise it is published without text (pending while the AI can translate)
    /// and handed to the AI lane. Every such line goes to the lane, also while the settings have a config error: that
    /// error then reaches the status and the lane settles the entry as untranslated. Tick only; never throws.
    /// </summary>
    /// <param name="memory">
    /// Bubbles zones: the zone's bubbles already translated. Each bubble is then known from the library or else from
    /// the memory, and only the bubbles neither has go to the AI. Null (Block zones): the library has the whole line or
    /// the whole line goes to the AI.
    /// </param>
    private void Publish(StableLine line, PartMemory? memory)
    {
        var library = LibraryFor(line); // read once: the lookup and the AI job use the same library
        string? thai;
        IReadOnlyList<string?>? known = null;
        if (memory is null || line.Reads.Count == 0)
        {
            thai = LookUpLibrary(library, line);
        }
        else
        {
            known = LookUpParts(library, line, memory);
            thai = known.All(k => k is not null) ? string.Join('\n', known) : null;
            if (thai is not null || known.All(k => k is null))
                known = null; // all known, or none: nothing to merge
        }
        SubtitleEntry? entry = null;
        try
        {
            entry = _hub.Publish(line.Text, translated: thai, line.Source.Code, error: null,
                awaitingAi: thai is null && _aiRefiner.IsActive, bounds: line.Bounds,
                dialogue: line.HasNames ? line.Dialogue : null, speakers: line.HasNames ? SpeakerTags(line) : null,
                rowBounds: line.RowBounds, zoneId: line.ZoneId, zoneName: line.ZoneName, sendToWeb: line.SendToWeb);
        }
        catch (Exception ex)
        {
            // A hub subscriber threw. Swallowed so later lines keep coming.
            Log.Warn($"Publishing a subtitle failed: {ex.GetType().Name}");
        }
        if (entry is null)
            return;
        if (thai is not null)
            _aiRefiner.RememberTranslated(entry);
        else
            _aiRefiner.OnPublished(entry, new AiJobContext(line.Source, line.TargetLang, line.GameTitle, line.ContextLines,
                line.HasNames ? [.. line.Reads.Select(r => r.Name)] : null, library, line.LearnToLibrary,
                known, memory));
    }

    /// <summary>
    /// Bubbles: each bubble's Thai, from the library (with its sender as the label) or else the zone's memory; null for
    /// a bubble neither has. One Thai line per bubble. The library counts one hit when it had every bubble, else one
    /// miss. Tick only; never throws.
    /// </summary>
    private static IReadOnlyList<string?> LookUpParts(ActiveLibrary? library, StableLine line, PartMemory memory)
    {
        var known = new string?[line.Reads.Count];
        bool allFromLibrary = library is not null;
        for (int i = 0; i < known.Length; i++)
        {
            var read = line.Reads[i];
            string? thai = null;
            if (library is not null)
            {
                try
                {
                    if (library.Matcher.Lookup(read.Text, read.Name) is { } hit)
                        thai = SpeakerText.Clean(hit.Thai).Replace('\n', ' '); // a learned answer may span lines
                }
                catch (Exception ex)
                {
                    Log.Warn($"Library lookup failed: {ex.GetType().Name}"); // type only: no text in logs
                    library = null; // not counted: the lookup did not happen
                }
            }
            allFromLibrary &= thai is not null;
            known[i] = thai ?? memory.Get(read.Text);
        }
        if (library is not null)
        {
            if (allFromLibrary)
                library.CountHit();
            else
                library.CountMiss();
        }
        return known;
    }

    /// <summary>
    /// The line's Thai from the library: each speaker's line (or the whole text without names) looked up on its own,
    /// joined with <c>\n</c>. Null when there is no library, any part misses or the lookup fails. Tick only; never throws.
    /// </summary>
    private static string? LookUpLibrary(ActiveLibrary? library, StableLine line)
    {
        if (library is null)
            return null;
        try
        {
            IEnumerable<(string Text, string? Label)> parts = line.HasNames
                ? line.Reads.Select(r => (r.Text, r.Name))
                : [(line.Text, null)];
            var thai = new List<string>();
            bool missed = false;
            foreach (var (text, label) in parts) // every part is looked up, so the hit or miss count covers the whole line
            {
                if (library.Matcher.Lookup(text, label) is not { } hit)
                {
                    missed = true;
                    continue;
                }
                // One line per speaker: a learned answer may span lines.
                thai.Add(line.HasNames ? SpeakerText.Clean(hit.Thai).Replace('\n', ' ') : hit.Thai);
            }
            if (missed)
            {
                library.CountMiss();
                return null;
            }
            library.CountHit();
            return string.Join('\n', thai);
        }
        catch (Exception ex)
        {
            Log.Warn($"Library lookup failed: {ex.GetType().Name}"); // type only: no text in logs
            return null;
        }
    }

    /// <summary>The library for this line, or null: libraries are English → Thai only (a cheap guard; the app drops the
    /// library on a language change too).</summary>
    private ActiveLibrary? LibraryFor(StableLine line) =>
        LibraryHolder.AppliesTo(line.Source.Code, line.TargetLang) ? _library?.Current : null;

    // Name lookups running alongside the AI lane.
    private readonly Lock _nameGate = new();
    private readonly List<Task> _nameLookups = [];

    private void TrackNameLookup(Task lookup)
    {
        lock (_nameGate)
            _nameLookups.Add(lookup);
        _ = lookup.ContinueWith(t =>
        {
            lock (_nameGate)
                _nameLookups.Remove(t);
        }, TaskScheduler.Default);
    }

    /// <summary>Completes when the name lookups started so far have finished (tests).</summary>
    internal Task WhenNamesIdleAsync()
    {
        lock (_nameGate)
            return Task.WhenAll([.. _nameLookups]);
    }

    /// <summary>
    /// Translates the speaker names the book does not know yet with the AI. Without a usable AI, or when the lookup
    /// fails, the original name stays; it is tried again after a minute. Never throws.
    /// </summary>
    private async Task LearnNamesAsync(StableLine line)
    {
        try
        {
            var book = Volatile.Read(ref _book);
            if (book is null)
                return;
            // Names the library has a Thai spelling for are shown from it: no lookup needed.
            var library = LibraryFor(line);
            var names = book.NamesToTranslate(line.Reads.Where(r => r.Name is not null).Select(r => r.Name!))
                .Where(n => library is null || string.IsNullOrEmpty(library.Characters.Resolve(n)?.ThName))
                .ToList();
            if (names.Count == 0)
                return;
            foreach (var name in names)
                book.MarkAttempt(name);
            // Null on any failure (logged by the refiner, type only).
            var thai = await _aiRefiner.TranslateNamesAsync(names,
                new AiJobContext(line.Source, line.TargetLang, line.GameTitle, 0), line.Ct).ConfigureAwait(false);
            if (thai is null)
                return;
            for (int i = 0; i < names.Count; i++)
            {
                if (thai[i].Length > 0 && book.SetThai(names[i], thai[i]))
                    NotifyLearned(line.ProfileName, names[i], book);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Speaker name translation failed: {ex.GetType().Name}"); // type only: no names in logs
        }
    }

    /// <summary>
    /// Samples the colour of each speaker label the book has no colour for, from the raw crop (OCR boxes are scaled
    /// back by <paramref name="ocrScale"/>). Tick only; never throws.
    /// </summary>
    private void LearnNameColors(IReadOnlyList<SpeakerRead> reads, Frame raw, double ocrScale, string? profileName)
    {
        var book = Volatile.Read(ref _book);
        if (book is null)
            return;
        foreach (var read in reads)
        {
            if (read is not { Name: { } name, LabelBox: { } box } || !book.IsTrusted(name) || !SpeakerPalette.IsMissing(book.Get(name)?.Color))
                continue;
            try
            {
                var rawBox = new PixelRect((int)Math.Floor(box.X / ocrScale), (int)Math.Floor(box.Y / ocrScale),
                    (int)Math.Ceiling(box.Width / ocrScale), (int)Math.Ceiling(box.Height / ocrScale));
                if (NameColorSampler.Sample(raw, rawBox) is { } color && book.SetColorIfMissing(name, color))
                    NotifyLearned(profileName, name, book);
            }
            catch (Exception ex)
            {
                Log.Warn($"Name colour sampling failed: {ex.GetType().Name}");
            }
        }
    }

    private void NotifyLearned(string? profileName, string name, SpeakerBook book)
    {
        if (_onSpeakerLearned is null || book.Get(name) is not { } info)
            return;
        try
        {
            _onSpeakerLearned(profileName ?? "", name, info);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving a speaker name failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// One tag per line: the Thai name when known (the library's character first, then the speaker book; otherwise the
    /// name as read) and its colour.
    /// </summary>
    /// <remarks>
    /// A name never shows white: without the game's colour (or when it read as white) it gets its palette colour, and
    /// two different speakers on screen together never share a palette colour.
    /// </remarks>
    private IReadOnlyList<SpeakerTag?> SpeakerTags(StableLine line)
    {
        var book = Volatile.Read(ref _book);
        var characters = LibraryFor(line)?.Characters;
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name → colour on screen
        var tags = new List<SpeakerTag?>(line.Reads.Count);
        foreach (var read in line.Reads)
        {
            if (read.Name is not { } name)
            {
                tags.Add(null);
                continue;
            }
            var info = book?.Get(name);
            if (!used.TryGetValue(name, out var color))
            {
                color = info?.Color;
                if (SpeakerPalette.IsMissing(color))
                {
                    color = SpeakerPalette.For(name);
                    for (int i = 0; i < SpeakerPalette.Colors.Count && used.ContainsValue(color); i++)
                        color = SpeakerPalette.After(color);
                }
                used[name] = color!;
            }
            var thai = characters?.Resolve(name)?.ThName;
            tags.Add(new SpeakerTag(string.IsNullOrEmpty(thai) ? info?.Thai ?? name : thai, line.SpeakerColors ? color : null));
        }
        return tags;
    }

    private sealed record WindowSnapshot(WindowInfo? Window);

    /// <summary>
    /// A zone's settings generation. <see cref="Read"/> changes with anything that changes what its OCR reads (an OCR
    /// result from an older one is dropped); <see cref="Reset"/> with anything that also restarts its stability filter.
    /// <see cref="Context"/> is the same for every zone and changes when the AI context resets (source, game, target
    /// language or profile): what the zone's bubbles were translated to no longer applies.
    /// </summary>
    private readonly record struct ZoneEpoch(int Read, int Reset, int Context);

    /// <summary>One zone's reading state. Tick only.</summary>
    private sealed class ZoneState(StabilityOptions stability, ZoneEpoch epoch)
    {
        public StabilityFilter Filter { get; } = new(stability);
        public TriggerGate Gate { get; } = new();
        public LogThrottle OcrFaults { get; } = new();
        public ZoneEpoch Epoch { get; private set; } = epoch;
        public LastRead? LastRead { get; set; }
        // The frame the trigger box was last read from (compared by reference) and whether its words were there.
        public Frame? TriggerFrame { get; set; }
        public bool TriggerMatched { get; set; }
        // The text last published for this zone; null after a clear or a reset.
        public string? Shown { get; set; }
        // Bubbles zones with a trigger: the zone was hidden because its text moved; set until the next publish.
        public bool HiddenWhileMoving { get; set; }
        // Bubbles mode: the bubbles translated so far. Kept for the session; a new one when the zone's reading or the AI
        // context changes (an answer still in flight fills the old one, which is dropped).
        public PartMemory Memory { get; private set; } = new();

        /// <summary>
        /// Catches up with the zone's settings: drops the earlier reads, restarts the filter if needed, and forgets the
        /// bubbles when the filter restarts (not for a filter change only: the same text is still the same text) or
        /// the AI context resets. True when the filter restarted: the caller hides what the zone showed.
        /// </summary>
        public bool Apply(ZoneEpoch epoch)
        {
            bool reset = epoch.Reset != Epoch.Reset;
            if (epoch.Read != Epoch.Read)
            {
                LastRead = null;
                TriggerFrame = null;
            }
            if (reset)
                ResetFilter();
            if (reset || epoch.Context != Epoch.Context)
                Memory = new();
            Epoch = epoch;
            return reset;
        }

        public void ResetFilter()
        {
            Filter.Reset();
            Shown = null;
            HiddenWhileMoving = false;
        }
    }

    /// <summary>One box's OCR result and the crop it was read from (<paramref name="BoxPx"/> in frame pixels).</summary>
    private sealed record BoxRead(IReadOnlyList<OcrLine> Lines, Frame Raw, double OcrScale, PixelRect BoxPx);

    /// <summary>A zone's last OCR result and the frame it was read from (compared by reference).</summary>
    private sealed record LastRead(Frame Frame, string? Text, RegionFraction? Bounds,
        IReadOnlyList<RegionFraction>? RowBounds, IReadOnlyList<SpeakerRead> Reads, Frame Raw, double OcrScale);

    private sealed record Capture(Frame Raw, Frame Processed);

    /// <summary>A stable line, with the config it was read under, its zone and where it was on screen.</summary>
    private sealed record StableLine(string Text, SourceLanguage Source, string TargetLang, string? GameTitle,
        int ContextLines, CancellationToken Ct, RegionFraction? Bounds, IReadOnlyList<RegionFraction>? RowBounds,
        IReadOnlyList<SpeakerRead> Reads,
        string? ProfileName, string ZoneId, string ZoneName, bool SendToWeb, bool LearnToLibrary, bool SpeakerColors = true)
    {
        /// <summary>At least one line has a speaker name: names and dialogue are handled apart.</summary>
        public bool HasNames => Reads.Any(r => r.Name is not null);

        /// <summary>What gets translated when <see cref="HasNames"/>: the lines without their names.</summary>
        public string Dialogue => string.Join('\n', Reads.Select(r => r.Text));
    }
}
