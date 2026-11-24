using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Hub;

/// <summary>
/// Latest subtitles, history and pipeline status, shared by the overlay and the web server. Thread-safe: state is
/// guarded by one lock and <see cref="Changed"/> is raised after the lock is released, on the caller's thread.
/// Events from different threads can therefore arrive interleaved; handlers must not assume a single thread.
/// </summary>
public sealed class SubtitleHub(TimeProvider time, int capacity = 100)
{
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
    private readonly Lock _gate = new();
    private readonly Queue<SubtitleEntry> _history = new();
    private long _lastId;
    private PipelineStatus _status = PipelineStatus.Initial;
    private int _webClients;

    public event EventHandler<HubEvent>? Changed;

    public PipelineStatus Status { get { lock (_gate) return _status; } }

    public int WebClients { get { lock (_gate) return _webClients; } }

    /// <param name="bounds">Where the game drew <paramref name="source"/> (fraction of the client area), if known.</param>
    /// <param name="dialogue">With speaker names: the source without them (see <see cref="SubtitleEntry.Dialogue"/>).</param>
    /// <param name="speakers">With speaker names: one tag per line (see <see cref="SubtitleEntry.Speakers"/>).</param>
    public SubtitleEntry Publish(string source, string? translated, string sourceLang, string? error, bool awaitingAi = false,
        RegionFraction? bounds = null, string? dialogue = null, IReadOnlyList<SpeakerTag?>? speakers = null,
        IReadOnlyList<RegionFraction>? rowBounds = null, string? zoneId = null, string? zoneName = null,
        bool sendToWeb = true)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceLang);
        SubtitleEntry entry;
        lock (_gate)
        {
            entry = new SubtitleEntry(++_lastId, time.GetUtcNow(), source, translated, sourceLang, error,
                awaitingAi, bounds, dialogue, speakers, rowBounds, zoneId, zoneName, sendToWeb);
            _history.Enqueue(entry);
            while (_history.Count > _capacity)
                _history.Dequeue();
        }
        Raise(new SubtitlePublished(entry));
        return entry;
    }

    /// <summary>
    /// Replaces the entry with the given id in place (same position and time) and raises <see cref="SubtitleRefined"/>.
    /// A non-null <paramref name="translated"/> becomes the translation and clears the error; null only clears
    /// <see cref="SubtitleEntry.AwaitingAi"/>. Returns null, raising nothing, when the entry has left the history.
    /// </summary>
    /// <param name="dropSpeakers">The translation's lines no longer match the speakers: show it without names.</param>
    public SubtitleEntry? Refine(long id, string? translated, bool dropSpeakers = false)
    {
        SubtitleEntry? refined = null;
        lock (_gate)
        {
            var items = _history.ToArray();
            var index = Array.FindIndex(items, e => e.Id == id);
            if (index < 0)
                return null;
            var old = items[index];
            refined = translated is null
                ? old with { AwaitingAi = false }
                : old with { Translated = translated, Error = null, AwaitingAi = false };
            if (dropSpeakers)
                refined = refined with { Speakers = null, Dialogue = null };
            items[index] = refined;
            _history.Clear();
            foreach (var e in items)
                _history.Enqueue(e);
        }
        Raise(new SubtitleRefined(refined));
        return refined;
    }

    /// <param name="zoneId">The zone to clear; null clears all zones.</param>
    public void Clear(string? zoneId = null) => Raise(new SubtitleCleared(zoneId));

    /// <summary>Hides the zone now and drops its waiting lines and late answers; see <see cref="ZoneHidden"/>.</summary>
    public void HideZone(string zoneId)
    {
        ArgumentNullException.ThrowIfNull(zoneId);
        Raise(new ZoneHidden(zoneId));
    }

    public void SetTrigger(string zoneId, bool active)
    {
        ArgumentNullException.ThrowIfNull(zoneId);
        Raise(new TriggerChanged(zoneId, active));
    }

    /// <summary>Raises <see cref="StatusChanged"/> only when the value differs from the current status.</summary>
    public void SetStatus(PipelineStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        lock (_gate)
        {
            if (s == _status)
                return;
            _status = s;
        }
        Raise(new StatusChanged(s));
    }

    /// <summary>Oldest to newest.</summary>
    public IReadOnlyList<SubtitleEntry> GetHistory()
    {
        lock (_gate)
            return [.. _history];
    }

    public void ClientConnected()
    {
        int count;
        lock (_gate)
            count = ++_webClients;
        Raise(new ClientsChanged(count));
    }

    public void ClientDisconnected()
    {
        int count;
        lock (_gate)
        {
            if (_webClients == 0)
                return;
            count = --_webClients;
        }
        Raise(new ClientsChanged(count));
    }

    private void Raise(HubEvent e) => Changed?.Invoke(this, e);
}
