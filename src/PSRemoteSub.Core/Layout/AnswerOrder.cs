using PSRemoteSub.Core.Hub;

namespace PSRemoteSub.Core.Layout;

/// <summary>
/// Lines published without text, waiting for the AI, and the answers that came back before an older line's (requests
/// sent at once may finish in any order). An answer is handed over only once no older line is still waiting, so the
/// overlay gets them in the order of their lines. Not thread-safe (UI thread).
/// </summary>
public sealed class AnswerOrder
{
    private readonly SortedSet<long> _pending = [];
    private readonly SortedDictionary<long, SubtitleEntry> _answered = [];

    /// <summary>The line <paramref name="id"/> was published without text: its answer is expected.</summary>
    public void Expect(long id) => _pending.Add(id);

    /// <summary>True while the line is waiting for its answer or for an older line's.</summary>
    public bool Holds(long id) => _pending.Contains(id) || _answered.ContainsKey(id);

    /// <summary>
    /// The AI answered (or settled without text) a line this holds. Returns the answers now due, oldest first: none while
    /// an older line still waits. An answer without text is skipped when a newer line is waiting or due with it: the
    /// game has moved on, and "(untranslated)" is not worth a turn before it.
    /// </summary>
    public IReadOnlyList<SubtitleEntry> Answer(SubtitleEntry entry)
    {
        if (!Holds(entry.Id))
            return [];
        _pending.Remove(entry.Id);
        _answered[entry.Id] = entry; // also a later refinement of an answer still waiting for its turn
        List<SubtitleEntry>? due = null;
        while (_answered.Count > 0)
        {
            var (id, answer) = _answered.First();
            if (_pending.Count > 0 && _pending.Min < id)
                break;
            _answered.Remove(id);
            if (answer.Translated is null && (_pending.Count > 0 || _answered.Count > 0))
                continue;
            (due ??= []).Add(answer);
        }
        return due ?? [];
    }

    /// <summary>Forget everything (pause, another Remote Play window).</summary>
    public void Reset()
    {
        _pending.Clear();
        _answered.Clear();
    }
}
