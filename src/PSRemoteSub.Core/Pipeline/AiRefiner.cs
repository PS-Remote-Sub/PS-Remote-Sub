using PSRemoteSub.Core.Diagnostics;
using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Text;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// The translation lane: takes each entry after the pipeline has published it (pending, without text), asks whichever
/// translator the source gives now (an AI model, with the previous lines as context, or Google Translate, text only) and
/// fills the entry's text in through <see cref="SubtitleHub.Refine"/>. An entry it cannot translate is settled as untranslated.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Queue: at most <see cref="MaxParallel"/> requests in flight, shared across zones (one by default, so each
/// line's context has the translations before it; with more, a line's context has only the translations back so far).
/// Newer entries wait in order and are sent oldest first across zones as a request finishes. Context pairs and the
/// <see cref="MaxWaiting"/> limit are kept per zone (<see cref="SubtitleEntry.ZoneId"/>; null is a zone of its own), so
/// a burst in one zone neither drops nor pollutes another's lines. Answers may come back out of order: the overlay shows them in
/// order. At most <see cref="MaxWaiting"/> of a zone wait: when another comes, the oldest waiting
/// entry of that zone is settled at once (<c>Refine(id, null)</c>, which only clears <see cref="SubtitleEntry.AwaitingAi"/>).</item>
/// <item>Every entry that will not be sent (paused, config error, dropped from a full queue, failed) is settled the same way, so
/// <see cref="SubtitleEntry.AwaitingAi"/> never sticks. After <see cref="Dispose"/> entries are dropped silently.</item>
/// <item>Backoff: <c>Unavailable</c>/<c>BadResponse</c> (and any unexpected exception) pause 30 s, <c>RateLimited</c>
/// 60 s, <c>Auth</c> and <c>ModelNotFound</c> until <see cref="AiTranslatorHandle.Generation"/> changes. A new
/// generation clears every pause and <see cref="PipelineStatus.AiError"/>; generations only increase, so an older
/// handle is ignored. Results from a translator that is no longer the source's current one (older generation) still
/// refine their line but never touch the status or pauses: the source is re-read under the
/// lock before either is changed. A settings change is applied at once through <see cref="Refresh"/>, else on the
/// next line. Times come from the <see cref="TimeProvider"/>.</item>
/// <item>Threading: <see cref="OnPublished"/> may be called from any thread; the lane's continuations run on whatever
/// thread the translator completes on. All state is guarded by one lock. <c>updateStatus</c> and hub calls are made
/// outside it, because both raise hub events whose subscribers run inline.</item>
/// <item>Status: the lane keeps the AI status it wants (error key and retry time) under its lock, and every push
/// writes that latest value rather than a delta, so pushes racing from two threads cannot leave a stale error behind.
/// The push function takes the lock briefly while the pipeline holds its status lock (order: pipeline status lock,
/// then this lock; never the reverse). <see cref="IAiTranslatorSource.Current"/> is also read under this lock; it is
/// a plain (volatile) read that takes no lock of its own.</item>
/// <item>Library: with a library in the job's context (<see cref="AiJobContext.Library"/>, the one the line was read
/// under) each request carries the game notes (built outside the lock), and each
/// refined answer is taught to the matcher in memory (outside the lock; only when <see cref="AiJobContext.Learn"/>), so the line shows
/// without the AI for the rest of the session. Nothing is saved to disk. A line the library already had comes through <see cref="RememberTranslated"/> as context only.</item>
/// <item>Bubbles: with <see cref="AiJobContext.KnownParts"/> only the parts not known yet are sent, and the answer is
/// merged back in order (<see cref="PartMerge"/>); with <see cref="AiJobContext.Memory"/> each part the answer translated
/// is put in the zone's memory (outside the lock; the memory has its own). Such a job learns only the parts the answer
/// translated, one line each, since bubbles are looked up one by one.</item>
/// <item>Google (<see cref="TranslatorKind.Google"/>, the kind of the handle a job is sent with): the request carries
/// the text only (no context lines, no game notes), <see cref="TranslateNamesAsync"/> returns null without a call, and
/// answers are not taught to the matcher (the zone's bubble memory is still filled). Its failures that pause 30 s show
/// <see cref="GoogleDownKey"/> instead of <see cref="AiDownKey"/>; the other kinds keep their keys.</item>
/// <item>Never throws out of <see cref="OnPublished"/>, <see cref="RememberTranslated"/> or the lane. Logs only the
/// error kind and HTTP status.</item>
/// </list>
/// </remarks>
public sealed class AiRefiner : IDisposable
{
    /// <summary>Most context pairs sent before an entry (pairs are kept for the waiting entries too).</summary>
    public const int MaxHistory = 10;

    /// <summary>Default for <see cref="MaxWaiting"/>.</summary>
    public const int DefaultMaxWaiting = 3;

    /// <summary>The range <see cref="MaxWaiting"/> is held to.</summary>
    public const int MinMaxWaiting = 1, MaxMaxWaiting = 30;

    /// <summary>The range <see cref="MaxParallel"/> is held to; 1 sends one line at a time.</summary>
    public const int MinMaxParallel = 1, MaxMaxParallel = 10;

    public const string AiDownKey = "AiDown";
    public const string AiRateLimitedKey = "AiRateLimited";
    public const string AiAuthKey = "AiAuth";
    public const string AiModelNotFoundKey = "AiModelNotFound";
    public const string AiBillingKey = "AiBilling";

    /// <summary>Google Translate failed (unavailable, bad answer or unknown error): paused 30 s as for <see cref="AiDownKey"/>.</summary>
    public const string GoogleDownKey = "GoogleDown";

    private static readonly TimeSpan UnavailablePause = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RateLimitedPause = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Unusable answers in a row (see <see cref="AiTranslationException.UnusableAnswer"/>) before the lane pauses as
    /// for <c>Unavailable</c>: one odd answer only loses its own line, but a model that never gives a usable one is
    /// reported and not asked again for every line.
    /// </summary>
    public const int MaxUnusableInARow = 3;

    private readonly SubtitleHub _hub;
    private readonly IAiTranslatorSource _source;
    private readonly TimeProvider _time;
    private readonly Action<Func<PipelineStatus, PipelineStatus>> _updateStatus;
    private readonly Func<PipelineStatus, PipelineStatus> _withAiStatus;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly LogThrottle _faults = new();

    private readonly Lock _gate = new();
    private bool _disposed;
    // Queue lane: up to _maxParallel requests in flight, the rest wait in order.
    private int _inFlight;
    private readonly List<Job> _waiting = [];
    private int _maxWaiting = DefaultMaxWaiting;
    private int _maxParallel = MinMaxParallel;
    // Answers in flight at once are taught one at a time; _learnedOrder keeps the first one first.
    private readonly Lock _learnGate = new();
    private int _learnedOrder;
    private TaskCompletionSource? _idle;
    // Context pairs of every zone, oldest first.
    private readonly List<Pair> _history = [];
    // Pauses, for the generation last seen.
    private long? _generation;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private int _unusableInARow;
    // Auth or ModelNotFound: stopped until the generation changes.
    private long? _stoppedGeneration;
    // The AI status this lane wants shown.
    private string? _aiError;
    private DateTimeOffset? _aiRetryAt;

    /// <param name="updateStatus">
    /// Merges an AI status change into the pipeline status (normally <see cref="SubtitlePipeline.UpdateAiStatus"/>).
    /// It may be late-bound to a pipeline built after this refiner; see the <c>aiRefiner</c> parameter of the
    /// <see cref="SubtitlePipeline"/> constructor.
    /// </param>
    public AiRefiner(SubtitleHub hub, IAiTranslatorSource source, TimeProvider time,
        Action<Func<PipelineStatus, PipelineStatus>> updateStatus)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(updateStatus);
        _hub = hub;
        _source = source;
        _time = time;
        _updateStatus = updateStatus;
        _withAiStatus = s =>
        {
            lock (_gate)
                return s with { AiError = _aiError, AiRetryAt = _aiRetryAt };
        };
    }

    /// <summary>
    /// Most entries waiting for the request in flight (held to <see cref="MinMaxWaiting"/>..<see cref="MaxMaxWaiting"/>): a
    /// slow AI must not fall far behind the game. A lower value applies when the next entry comes. Thread-safe.
    /// </summary>
    public int MaxWaiting
    {
        get { lock (_gate) return _maxWaiting; }
        set { lock (_gate) _maxWaiting = Math.Clamp(value, MinMaxWaiting, MaxMaxWaiting); }
    }

    /// <summary>
    /// Most requests in flight at once (held to <see cref="MinMaxParallel"/>..<see cref="MaxMaxParallel"/>). A higher
    /// value starts waiting lines when the next entry comes; a lower one lets the requests in flight finish. Thread-safe.
    /// </summary>
    public int MaxParallel
    {
        get { lock (_gate) return _maxParallel; }
        set { lock (_gate) _maxParallel = Math.Clamp(value, MinMaxParallel, MaxMaxParallel); }
    }

    /// <summary>A usable translator: entries should be published as awaiting AI. Never throws.</summary>
    public bool IsActive => CurrentHandle() is { Translator: not null, ConfigErrorKey: null };

    /// <summary>
    /// Transliterates speaker names with the current AI translator: one result per name, in order. Null when there is no
    /// usable translator, the current one is Google, the lane is paused or stopped, the answer has another number of lines, or the call fails.
    /// Does not touch the AI status (the subtitle's own request reports problems). Thread-safe; never throws.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TranslateNamesAsync(IReadOnlyList<string> names, AiJobContext ctx,
        CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(ctx);
            if (names.Count == 0)
                return [];
            var handle = CurrentHandle();
            // Names are an AI request (AiRequestKind.Names): Google is never asked.
            if (handle is not { Translator: { } translator, ConfigErrorKey: null, Kind: TranslatorKind.Ai })
                return null;
            lock (_gate)
            {
                if (_disposed || _time.GetUtcNow() < _pausedUntil || _stoppedGeneration == handle.Generation)
                    return null;
            }
            var request = new AiTranslationRequest(string.Join('\n', names), [], LanguageNames.ForSource(ctx.Source),
                LanguageNames.ForTarget(ctx.TargetCode), ctx.GameTitle, AiRequestKind.Names);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var text = await translator.TranslateAsync(request, linked.Token).ConfigureAwait(false);
            var result = text.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            return result.Length == names.Count ? result : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"AI name translation failed: {ex.GetType().Name}"); // type only: no names in logs
            return null;
        }
    }

    /// <summary>
    /// Called after the pipeline published <paramref name="entry"/>, for every line (also while the settings have a
    /// config error, so that the error reaches the status and the entry is settled). Thread-safe; never throws.
    /// </summary>
    public void OnPublished(SubtitleEntry entry, AiJobContext ctx)
    {
        try
        {
            OnPublishedCore(entry, ctx);
        }
        catch (Exception ex)
        {
            LogFault("AI lane failed", ex);
        }
    }

    /// <summary>
    /// Called instead of <see cref="OnPublished"/> for an entry published already translated (from the library): its
    /// pair becomes context for the lines after it. Touches neither the queue nor the AI status. Thread-safe; never throws.
    /// </summary>
    public void RememberTranslated(SubtitleEntry entry)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(entry);
            lock (_gate)
            {
                if (!_disposed)
                    Remember(entry);
            }
        }
        catch (Exception ex)
        {
            LogFault("AI lane failed", ex);
        }
    }

    /// <summary>
    /// Re-reads the source now and applies a settings change without waiting for the next subtitle line: a new
    /// generation clears the AI error and every pause (or shows its config error). AppHost calls this right after
    /// <c>AiSource.Rebuild()</c>. Thread-safe; never throws; does nothing once disposed.
    /// </summary>
    public void Refresh()
    {
        try
        {
            var handle = CurrentHandle();
            if (handle is null)
                return; // The source failed (logged); nothing to apply.
            bool statusChanged;
            lock (_gate)
            {
                if (_disposed)
                    return;
                statusChanged = Observe(handle) is HandleState.Changed;
            }
            if (statusChanged)
                PushStatus();
        }
        catch (Exception ex)
        {
            LogFault("AI refresh failed", ex);
        }
    }

    /// <summary>Forgets the context lines (source language, game or target changed). Thread-safe.</summary>
    public void ResetContext()
    {
        lock (_gate)
            _history.Clear();
    }

    /// <summary>Test hook: completes when no request is in flight or pending.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
        {
            if (_inFlight == 0)
                return Task.CompletedTask;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task;
        }
    }

    /// <summary>Cancels the request in flight and drops the waiting entries. Later entries are ignored.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _waiting.Clear();
        }
        _disposeCts.Cancel(); // Not disposed: the lane may still read its token.
    }

    private void OnPublishedCore(SubtitleEntry entry, AiJobContext ctx)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(ctx);
        var handle = CurrentHandle();
        var job = new Job(entry, ctx, _time.GetTimestamp());
        List<Job>? dropped = null, start = null;
        bool send, statusChanged;
        lock (_gate)
        {
            if (_disposed)
                return;
            Remember(entry);
            send = Admit(handle, out statusChanged);
            if (send)
            {
                // Behind any line still waiting (MaxParallel may have been raised): the oldest are sent first.
                _waiting.Add(job);
                while (_inFlight < _maxParallel && TryDequeue(out var next))
                {
                    _inFlight++;
                    (start ??= []).Add(next);
                }
                // MaxWaiting counts per zone: a burst in one zone never drops another zone's line.
                while (_waiting.Count(j => j.Entry.ZoneId == entry.ZoneId) > _maxWaiting)
                {
                    int oldest = _waiting.FindIndex(j => j.Entry.ZoneId == entry.ZoneId);
                    (dropped ??= []).Add(_waiting[oldest]);
                    _waiting.RemoveAt(oldest);
                }
            }
        }

        if (statusChanged)
            PushStatus();
        if (!send)
            Settle(entry);
        foreach (var old in dropped ?? [])
            Settle(old.Entry);
        foreach (var next in start ?? [])
            _ = RunAsync(next);
    }

    /// <summary>
    /// Never throws. One of up to <see cref="MaxParallel"/> lanes: sends its job, then the oldest waiting one, until none
    /// waits (or there are more lanes than <see cref="MaxParallel"/> now allows).
    /// </summary>
    private async Task RunAsync(Job job)
    {
        while (true)
        {
            try
            {
                await ProcessAsync(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogFault("AI lane failed", ex);
                Settle(job.Entry); // Never leave an entry dim, whatever failed.
            }

            TaskCompletionSource? idle;
            lock (_gate)
            {
                if (_inFlight <= _maxParallel && TryDequeue(out var next))
                {
                    job = next;
                    continue;
                }
                _inFlight--;
                idle = null;
                if (_inFlight == 0)
                {
                    idle = _idle;
                    _idle = null;
                }
            }
            idle?.TrySetResult();
            return;
        }
    }

    /// <summary>Under <see cref="_gate"/>. Takes the oldest waiting job, whatever its zone.</summary>
    private bool TryDequeue(out Job job)
    {
        if (_waiting.Count == 0)
        {
            job = null!;
            return false;
        }
        job = _waiting[0];
        _waiting.RemoveAt(0);
        return true;
    }

    private async Task ProcessAsync(Job job)
    {
        // Read again: the settings may have changed (or a pause begun) while this job was pending.
        var handle = CurrentHandle();
        // The kind of the translator this job is sent to, not whatever is current when its answer comes back.
        var kind = handle?.Kind ?? TranslatorKind.Ai;
        AiTranslationRequest? request = null;
        bool statusChanged;
        lock (_gate)
        {
            if (_disposed)
                return;
            if (Admit(handle, out statusChanged))
                request = BuildRequest(job, kind);
        }

        if (statusChanged)
            PushStatus();
        if (request is null || handle?.Translator is not { } translator)
        {
            Settle(job.Entry);
            return;
        }
        // The library the line was read under (not the one in use now), for the notes sent and the lines learned from
        // the answer; English → Thai only, and AI only: Google gets no notes and its answers are not learned.
        var library = kind is TranslatorKind.Ai && LibraryHolder.AppliesTo(job.Ctx.Source.Code, job.Ctx.TargetCode)
            ? job.Ctx.Library
            : null;
        var known = KnownParts(job);
        if (library is not null)
            request = request with { Brief = BuildBrief(library, request.Text, SentLabels(job, known), handle.BriefBudget) };

        string answer;
        long sent = _time.GetTimestamp();
        try
        {
            answer = await translator.TranslateAsync(request, _disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return; // Disposed: drop.
        }
        catch (Exception ex)
        {
            LogTiming("failed", job, sent);
            Fail(job, handle.Generation, kind, ex);
            return;
        }
        LogTiming("translated", job, sent);

        // Bubbles: the answer has one line per part sent. Merged with the parts already known, line i is part i again;
        // when the lines do not match the parts, the answer as it came beats Thai on the wrong bubbles.
        var parts = Parts(job.Entry);
        bool bubbles = known is not null || job.Ctx.Memory is not null;
        string? merged = bubbles ? PartMerge.Merge(known ?? new string?[parts.Length], answer) : null;
        string text = known is not null && merged is not null ? merged : answer;
        bool dropSpeakers = known is not null && merged is null;

        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (var pair in _history)
                if (pair.Id == job.Entry.Id)
                    pair.Translation = text;
            // As in Fail: a translator from older settings says nothing about the current ones' status.
            statusChanged = IsCurrent(handle.Generation) && SetStatus(null, null);
            _unusableInARow = 0;
        }
        _faults.Reset();
        if (statusChanged)
            PushStatus();

        // One line per speaker: line i must still belong to speaker i. When the lines no longer match, the text
        // without names beats names on the wrong lines.
        if (job.Entry.Speakers is { Count: > 0 } speakers)
        {
            text = SpeakerText.Clean(text);
            dropSpeakers |= SpeakerText.LineCount(text) != speakers.Count;
        }
        var translated = merged is not null ? TranslatedParts(parts, known, answer) : [];
        if (job.Ctx.Memory is { } memory)
        {
            foreach (var (_, en, th) in translated)
                memory.Put(en, th);
        }
        if (!Refine(job.Entry.Id, text, dropSpeakers) || library is null || !job.Ctx.Learn)
            return;
        // Bubbles are looked up one by one, so they are learned one by one: only those this answer translated (the
        // others came from the library or the memory), and nothing when the answer did not match the bubbles.
        if (bubbles)
            LearnParts(library, job, translated);
        else
            Learn(library, job, text, dropSpeakers);
    }

    /// <summary>The entry's parts (bubbles): one per line of what is translated.</summary>
    private static string[] Parts(SubtitleEntry entry) => (entry.Dialogue ?? entry.Source).Split('\n');

    /// <summary>
    /// What is already known of each of the job's parts, when only some are sent (<see cref="AiJobContext.KnownParts"/>
    /// with one entry per part and at least one unknown); otherwise null and the whole line is sent.
    /// </summary>
    private static IReadOnlyList<string?>? KnownParts(Job job) =>
        job.Ctx.KnownParts is { } known && known.Count == Parts(job.Entry).Length && known.Any(k => k is null)
            ? known
            : null;

    /// <summary>The speaker labels of the lines sent: those of the unknown parts when only some are sent.</summary>
    private static IReadOnlyList<string?> SentLabels(Job job, IReadOnlyList<string?>? known)
    {
        var labels = job.Ctx.SpeakerLabels ?? [];
        return known is not null && labels.Count == known.Count ? [.. labels.Where((_, i) => known[i] is null)] : labels;
    }

    /// <summary>
    /// The parts the answer translated (those not <paramref name="known"/>; all when it is null), each with its index
    /// and its line of the answer. Only after a merge succeeded, so the answer has one line per unknown part.
    /// </summary>
    private static List<(int Index, string En, string Th)> TranslatedParts(string[] parts, IReadOnlyList<string?>? known,
        string answer)
    {
        var lines = PartMerge.AnswerLines(answer);
        var translated = new List<(int, string, string)>(lines.Count);
        for (int i = 0; i < parts.Length; i++)
        {
            if (known?[i] is null)
                translated.Add((i, parts[i], lines[translated.Count]));
        }
        return translated;
    }

    /// <summary>
    /// Outside <see cref="_gate"/>. Teaches each bubble the AI translated to the matcher (in memory), one line each,
    /// with its sender as the speaker when it has one. Never throws.
    /// </summary>
    private void LearnParts(ActiveLibrary library, Job job, List<(int Index, string En, string Th)> translated)
    {
        try
        {
            using var _ = _learnGate.EnterScope(); // answers in flight at once finish together
            var labels = job.Ctx.SpeakerLabels;
            foreach (var (i, en, th) in translated)
            {
                var label = labels is not null && i < labels.Count ? labels[i] : null;
                Teach(library, en, th, library.Characters.Resolve(label)?.En ?? label);
            }
        }
        catch (Exception ex)
        {
            LogFault("Learning a translation failed", ex);
        }
    }

    /// <summary>Outside <see cref="_gate"/>. The game notes for the text sent; null when there are none or on a fault.</summary>
    private GameBrief? BuildBrief(ActiveLibrary library, string text, IReadOnlyList<string?> labels, int budget)
    {
        try
        {
            var brief = GameBriefBuilder.Build(library.Data, library.Characters, text, labels, budget);
            return brief.Static.Length == 0 && brief.PerLine.Length == 0 ? null : brief;
        }
        catch (Exception ex)
        {
            LogFault("Building the game notes failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Outside <see cref="_gate"/>. Teaches the AI's translation of a refined line to the matcher (in memory):
    /// one line per speaker when the answer kept one line per speaker, else the whole line without a
    /// speaker. Never throws.
    /// </summary>
    private void Learn(ActiveLibrary library, Job job, string text, bool dropSpeakers)
    {
        try
        {
            using var _ = _learnGate.EnterScope(); // answers in flight at once finish together
            var entry = job.Entry;
            var labels = job.Ctx.SpeakerLabels;
            if (entry is { Speakers: { Count: > 0 } speakers, Dialogue: { } dialogue } && !dropSpeakers)
            {
                var en = dialogue.Split('\n');
                var th = text.Split('\n');
                if (en.Length == speakers.Count && th.Length == speakers.Count)
                {
                    for (int i = 0; i < en.Length; i++)
                    {
                        var label = labels is not null && i < labels.Count ? labels[i] : null;
                        var speaker = library.Characters.Resolve(label)?.En ?? label;
                        Teach(library, en[i], th[i], speaker);
                    }
                    return;
                }
            }
            Teach(library, entry.Dialogue ?? entry.Source, text, null);
        }
        catch (Exception ex)
        {
            LogFault("Learning a translation failed", ex);
        }
    }

    /// <summary>Teaches the matcher this answer for the rest of the session. Nothing is saved: the dev translates the library.</summary>
    private void Teach(ActiveLibrary library, string en, string th, string? speaker) =>
        library.Matcher.AddLearned(new LibraryLine(en, th, "ai", null, null, speaker, Interlocked.Increment(ref _learnedOrder)));

    /// <param name="translator">The kind of the translator the job was sent to: picks the key shown for a 30 s pause.</param>
    private void Fail(Job job, long generation, TranslatorKind translator, Exception ex)
    {
        AiErrorKind kind;
        bool unusable = ex is AiTranslationException { UnusableAnswer: true };
        string who = translator is TranslatorKind.Google ? "Google" : "AI";
        if (ex is AiTranslationException ai)
        {
            kind = ai.Kind;
            Log.Warn(unusable
                ? $"{who} answer unusable (not a plain translation); line left untranslated"
                : $"{who} translation failed: {ai.Kind} ({ai.StatusCode?.ToString() ?? "no status"})");
        }
        else
        {
            kind = AiErrorKind.Unavailable;
            LogFault($"{who} translation failed", ex);
        }

        var now = _time.GetUtcNow();
        bool statusChanged = false;
        lock (_gate)
        {
            if (_disposed)
                return;
            // A failure of a translator from older settings says nothing about the current ones. An unusable answer
            // loses only its own line, until too many come in a row.
            if (IsCurrent(generation) && !(unusable && ++_unusableInARow < MaxUnusableInARow))
            {
                _unusableInARow = 0;
                switch (kind)
                {
                    case AiErrorKind.Auth:
                        _stoppedGeneration = generation;
                        statusChanged = SetStatus(AiAuthKey, null);
                        break;
                    case AiErrorKind.ModelNotFound:
                        _stoppedGeneration = generation;
                        statusChanged = SetStatus(AiModelNotFoundKey, null);
                        break;
                    case AiErrorKind.Billing:
                        // No credit or quota left: retrying only repeats the rejection until the user acts.
                        _stoppedGeneration = generation;
                        statusChanged = SetStatus(AiBillingKey, null);
                        break;
                    case AiErrorKind.RateLimited:
                        _pausedUntil = now + RateLimitedPause;
                        statusChanged = SetStatus(AiRateLimitedKey, _pausedUntil);
                        break;
                    default:
                        _pausedUntil = now + UnavailablePause;
                        statusChanged = SetStatus(translator is TranslatorKind.Google ? GoogleDownKey : AiDownKey, _pausedUntil);
                        break;
                }
            }
        }
        if (statusChanged)
            PushStatus();
        Settle(job.Entry);
    }

    /// <summary>
    /// Under <see cref="_gate"/>. Whether to send now. Notes a new generation and a config error (see
    /// <see cref="Observe"/>); <paramref name="statusChanged"/> says whether the wanted status changed.
    /// </summary>
    private bool Admit(AiTranslatorHandle? handle, out bool statusChanged)
    {
        statusChanged = false;
        if (handle is null)
            return false;
        var state = Observe(handle);
        statusChanged = state is HandleState.Changed;
        // A stale handle (older generation, read before a rebuild) is not used: its settings are already replaced.
        if (state is HandleState.Stale || handle.ConfigErrorKey is not null || handle.Translator is null)
            return false;
        return _time.GetUtcNow() >= _pausedUntil && _stoppedGeneration != handle.Generation;
    }

    /// <summary>
    /// Under <see cref="_gate"/>. Whether a result from <paramref name="generation"/>'s translator may change the
    /// status and pauses: it is the generation last seen and still the source's current one (re-read now, so a
    /// rebuild not yet observed counts).
    /// </summary>
    private bool IsCurrent(long generation) =>
        _generation == generation && CurrentHandle()?.Generation == generation;

    /// <summary>
    /// Under <see cref="_gate"/>. Generations only increase: a newer one clears every pause and the error, an older one
    /// is ignored. The current generation's config error, if any, becomes the wanted status.
    /// </summary>
    private HandleState Observe(AiTranslatorHandle handle)
    {
        if (_generation is { } seen && handle.Generation < seen)
            return HandleState.Stale;
        bool changed = false;
        if (handle.Generation != _generation)
        {
            _generation = handle.Generation;
            ClearPauses();
            changed = SetStatus(null, null);
        }
        if (handle.ConfigErrorKey is { } key)
            changed |= SetStatus(key, null);
        return changed ? HandleState.Changed : HandleState.Unchanged;
    }

    /// <summary>Under <see cref="_gate"/>.</summary>
    private void ClearPauses()
    {
        _pausedUntil = DateTimeOffset.MinValue;
        _stoppedGeneration = null;
    }

    private enum HandleState { Unchanged, Changed, Stale }

    /// <summary>Under <see cref="_gate"/>. True when the wanted status changed.</summary>
    private bool SetStatus(string? error, DateTimeOffset? retryAt)
    {
        if (_aiError == error && _aiRetryAt == retryAt)
            return false;
        _aiError = error;
        _aiRetryAt = retryAt;
        return true;
    }

    /// <summary>Under <see cref="_gate"/>. Appends the entry's pair unless it repeats the last source exactly.</summary>
    private void Remember(SubtitleEntry entry)
    {
        var source = entry.Dialogue ?? entry.Source; // context pairs what was translated with its translation
        var zone = entry.ZoneId; // a zone's context is its own: null (old callers) is a zone too
        int last = _history.FindLastIndex(p => p.ZoneId == zone);
        if (last >= 0 && _history[last].Source == source)
        {
            // The repeat takes over the pair (keeping the best translation so far), so its own AI result lands there.
            _history[last].Id = entry.Id;
            _history[last].Translation ??= entry.Translated; // a library hit brings its translation along
            return;
        }
        _history.Add(new Pair(entry.Id, zone, source, entry.Translated));
        // MaxHistory pairs before each entry of the zone still to be sent (those in flight and those waiting, which are
        // older than the newest), so ContextLines = 10 (the top of its range) really sends 10.
        if (_history.Count(p => p.ZoneId == zone) > MaxHistory + _maxParallel + _maxWaiting)
            _history.RemoveAt(_history.FindIndex(p => p.ZoneId == zone));
    }

    /// <summary>
    /// Under <see cref="_gate"/>. Context is the last <c>ContextLines</c> pairs before the job's entry; none for Google,
    /// which translates the text alone.
    /// </summary>
    private AiTranslationRequest BuildRequest(Job job, TranslatorKind kind)
    {
        var pairs = _history.Where(p => p.ZoneId == job.Entry.ZoneId).ToList(); // only the job's zone
        int index = pairs.FindIndex(p => p.Id == job.Entry.Id);
        if (index < 0)
        {
            // Its pair is gone (context reset, or a later repeat took it over). Pairs are in id order: only the ones
            // before this entry are its previous lines; later lines (queued after it) never are.
            index = pairs.FindIndex(p => p.Id > job.Entry.Id);
        }
        int end = index >= 0 ? index : pairs.Count;
        int count = kind is TranslatorKind.Google ? 0 : Math.Clamp(job.Ctx.ContextLines, 0, end);
        var context = pairs.GetRange(end - count, count)
            .Select(p => new AiContextLine(p.Source, p.Translation))
            .ToList();
        // With speaker names only the dialogue is translated: the names are shown from the speaker book. Bubbles already
        // known are not sent again; the context keeps whole lines.
        var text = KnownParts(job) is { } known
            ? PartMerge.Unknown(Parts(job.Entry), known)
            : job.Entry.Dialogue ?? job.Entry.Source;
        return new AiTranslationRequest(text, context, LanguageNames.ForSource(job.Ctx.Source),
            LanguageNames.ForTarget(job.Ctx.TargetCode), job.Ctx.GameTitle,
            SourceCode: job.Ctx.Source.Code, TargetCode: job.Ctx.TargetCode);
    }

    private AiTranslatorHandle? CurrentHandle()
    {
        try
        {
            return _source.Current;
        }
        catch (Exception ex)
        {
            LogFault("AI translator source failed", ex);
            return null;
        }
    }

    /// <summary>Outside <see cref="_gate"/>. Writes the latest wanted AI status into the pipeline status.</summary>
    private void PushStatus()
    {
        try
        {
            _updateStatus(_withAiStatus);
        }
        catch (Exception ex)
        {
            LogFault("AI status update failed", ex);
        }
    }

    /// <summary>Outside <see cref="_gate"/>. Clears the entry's pending flag without changing its text.</summary>
    private void Settle(SubtitleEntry entry)
    {
        if (entry.AwaitingAi)
            Refine(entry.Id, null);
    }

    /// <summary>Outside <see cref="_gate"/>. Does nothing once disposed. True when the hub still had the entry.</summary>
    private bool Refine(long id, string? text, bool dropSpeakers = false)
    {
        if (Volatile.Read(ref _disposed))
            return false;
        try
        {
            return _hub.Refine(id, text, dropSpeakers) is not null;
        }
        catch (Exception ex)
        {
            // A hub subscriber threw (after the entry was refined). Swallowed so the lane keeps going.
            LogFault("Refining a subtitle failed", ex);
            return true;
        }
    }

    /// <summary>Type only: messages of unknown exceptions may echo the request.</summary>
    private void LogFault(string what, Exception ex)
    {
        if (_faults.ShouldLog(out int count))
            Log.Warn($"{what} (#{count}): {ex.GetType().Name}");
    }

    /// <summary>
    /// How long a line took: waiting in the queue, then the AI call, and how many lines still wait. Timings and counts
    /// only, so a slow AI can be told from a long queue in the log.
    /// </summary>
    private void LogTiming(string outcome, Job job, long sent)
    {
        int waiting;
        lock (_gate)
            waiting = _waiting.Count;
        double queued = _time.GetElapsedTime(job.Queued, sent).TotalSeconds;
        double ai = _time.GetElapsedTime(sent).TotalSeconds;
        Log.Info(FormattableString.Invariant($"AI line {outcome}: {ai:F1} s from the AI after {queued:F1} s queued, {waiting} waiting"));
    }

    /// <param name="Queued">When the line reached the lane (<see cref="TimeProvider.GetTimestamp"/>).</param>
    private sealed record Job(SubtitleEntry Entry, AiJobContext Ctx, long Queued);

    private sealed class Pair(long id, string? zoneId, string source, string? translation)
    {
        public long Id { get; set; } = id;
        public string? ZoneId { get; } = zoneId;
        public string Source { get; } = source;
        public string? Translation { get; set; } = translation;
    }
}
