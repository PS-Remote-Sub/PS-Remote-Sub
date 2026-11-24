using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Layout;

/// <summary>
/// A subtitle line on the overlay. <paramref name="ShownAt"/> is when it appeared on the overlay; <paramref name="Bounds"/>
/// is where the game drew it (fraction of the client area), if known; <paramref name="Speakers"/> names who says each
/// text line; <paramref name="RowBounds"/> is where the game drew each of its lines, if known.
/// </summary>
public sealed record OverlayLine(long Id, string Text, bool AwaitingAi, DateTimeOffset ShownAt, RegionFraction? Bounds = null,
    IReadOnlyList<SpeakerTag?>? Speakers = null, IReadOnlyList<RegionFraction>? RowBounds = null);

/// <summary>
/// The one line the overlay shows, held at least <see cref="MinDisplay"/> so it can be read. A newer line that comes
/// sooner waits (at most <see cref="MaxWaiting"/>, the oldest waiting line is dropped first). Once the game subtitle has
/// cleared, the last line hides when its time is up. Lines never go backwards: a line older than one already shown is
/// ignored. Every call takes the time; nothing runs by itself, so the caller calls <see cref="Advance"/> after
/// <see cref="NextChangeIn"/>. Not thread-safe (UI thread).
/// </summary>
public sealed class OverlayLines
{
    public const int DefaultMaxWaiting = 2;

    /// <summary>Most lines waiting for their turn (at least 1); a lower value applies when the next line comes.</summary>
    public int MaxWaiting
    {
        get;
        set => field = Math.Max(1, value);
    } = DefaultMaxWaiting;

    // Sorted by id.
    private readonly List<OverlayLine> _waiting = [];
    private long? _gameLine;
    // The newest line whose game subtitle has cleared: it (and older lines) may hide even once the game shows a newer one.
    private long? _lastClearedId;
    private long? _lastShownId;

    /// <summary>How long a line stays at least, once shown. Negative counts as zero.</summary>
    public TimeSpan MinDisplay { get; set; }

    public OverlayLine? Current { get; private set; }

    public void Show(long id, string text, bool awaitingAi, DateTimeOffset now, RegionFraction? bounds = null,
        IReadOnlyList<SpeakerTag?>? speakers = null, IReadOnlyList<RegionFraction>? rowBounds = null)
    {
        if (Current is { } current && current.Id == id)
        {
            Current = current with { Text = text, AwaitingAi = awaitingAi };
            return;
        }
        int index = _waiting.FindIndex(l => l.Id == id);
        if (index >= 0)
        {
            _waiting[index] = _waiting[index] with { Text = text, AwaitingAi = awaitingAi };
            return;
        }
        if (id <= _lastShownId)
            return;

        int at = _waiting.FindIndex(l => l.Id > id);
        _waiting.Insert(at < 0 ? _waiting.Count : at, new OverlayLine(id, text, awaitingAi, now, bounds, speakers, rowBounds));
        while (_waiting.Count > MaxWaiting)
            _waiting.RemoveAt(0);
        Advance(now);
    }

    /// <summary>Updates the line with <paramref name="id"/> (shown or waiting); true when the shown line changed.</summary>
    public bool Refine(long id, string text, bool awaitingAi)
    {
        if (Current is { } c && c.Id == id)
        {
            Current = c with { Text = text, AwaitingAi = awaitingAi };
            return true;
        }
        int index = _waiting.FindIndex(l => l.Id == id);
        if (index >= 0)
            _waiting[index] = _waiting[index] with { Text = text, AwaitingAi = awaitingAi };
        return false;
    }

    /// <summary>The line the game shows now, or null once its subtitle cleared (the last line may then hide).</summary>
    public void SetGameLine(long? id, DateTimeOffset now)
    {
        if (id is null && _gameLine is { } cleared && !(cleared <= _lastClearedId))
            _lastClearedId = cleared;
        _gameLine = id;
        Advance(now);
    }

    /// <summary>Moves on to the next waiting line, or hides a line whose time is up after the game cleared.</summary>
    /// <returns>True when <see cref="Current"/> changed.</returns>
    public bool Advance(DateTimeOffset now)
    {
        bool changed = false;
        while (true)
        {
            if (Current is null || IsDue(Current, now))
            {
                if (_waiting.Count > 0)
                {
                    var next = _waiting[0];
                    _waiting.RemoveAt(0);
                    Current = next with { ShownAt = now };
                    _lastShownId = next.Id;
                    changed = true;
                    continue;
                }
                if (Current is { } shown && MayHide(shown))
                {
                    Current = null;
                    changed = true;
                }
            }
            return changed;
        }
    }

    /// <summary>How long until <see cref="Advance"/> has something to do; null when only a new event can change things.</summary>
    public TimeSpan? NextChangeIn(DateTimeOffset now)
    {
        if (Current is not { } current || (_waiting.Count == 0 && !MayHide(current)))
            return null;
        if (current.ShownAt > now)
            return TimeSpan.Zero; // the clock went back: due now
        var left = current.ShownAt + Min - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Forget everything (pause, another Remote Play window).</summary>
    public void Reset()
    {
        _waiting.Clear();
        _gameLine = null;
        _lastClearedId = null;
        _lastShownId = null;
        Current = null;
    }

    // Its game subtitle is gone: everything cleared, or it is no newer than the last line the game cleared (a newer game
    // line pending its text must not keep an already-cleared line up). Without a clear, a line waits for the next one.
    private bool MayHide(OverlayLine line) => _gameLine is null || line.Id <= _lastClearedId;

    private TimeSpan Min => MinDisplay > TimeSpan.Zero ? MinDisplay : TimeSpan.Zero;

    // A line "from the future" (the wall clock was set back) is due: it must not stay up until the clock catches up.
    private bool IsDue(OverlayLine line, DateTimeOffset now) => line.ShownAt > now || now - line.ShownAt >= Min;
}
