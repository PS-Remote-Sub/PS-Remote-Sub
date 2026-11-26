namespace PSRemoteSub.Core.Notices;

/// <summary>
/// How a notice looks and lives: <see cref="Done"/> (green) goes away by itself; <see cref="Info"/> (blue) and
/// <see cref="Warn"/> (yellow) stay until fixed or closed.
/// </summary>
public enum NoticeKind { Done, Info, Warn }

/// <summary>A button on a notice. <paramref name="Id"/> is routed by the app (no callbacks live in the model).</summary>
public sealed record NoticeAction(string Label, string Id);

/// <summary>One notification (toast): kind, short title, optional body, buttons, and an optional topic.</summary>
/// <param name="Topic">
/// What the notice is about ("source", "update", …). A newer notice with the same topic replaces the shown one, so a
/// "fixed" notice takes the matching sticky one away. Null: no topic.
/// </param>
public sealed record Notice(NoticeKind Kind, string Title, string? Body, IReadOnlyList<NoticeAction> Actions, string? Topic)
{
    /// <summary>How long a <see cref="NoticeKind.Done"/> notice stays on screen.</summary>
    public static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(6);

    /// <summary>Only <see cref="NoticeKind.Done"/> goes away by itself (with a countdown bar); the others are sticky.</summary>
    public bool AutoDismiss => Kind == NoticeKind.Done;

    /// <summary>A green notice that goes away by itself.</summary>
    public static Notice Done(string title, string? body = null, string? topic = null, params NoticeAction[] actions) =>
        new(NoticeKind.Done, title, body, actions, topic);

    /// <summary>A blue notice that stays until closed or acted on.</summary>
    public static Notice Info(string title, string? body = null, string? topic = null, params NoticeAction[] actions) =>
        new(NoticeKind.Info, title, body, actions, topic);

    /// <summary>A yellow notice that stays until fixed or closed.</summary>
    public static Notice Warn(string title, string? body = null, string? topic = null, params NoticeAction[] actions) =>
        new(NoticeKind.Warn, title, body, actions, topic);

    /// <summary>Same kind, text, buttons and topic (records compare the action list by reference).</summary>
    public bool SameContent(Notice other) =>
        other.Kind == Kind && other.Title == Title && other.Body == Body && other.Topic == Topic
        && other.Actions.SequenceEqual(Actions);
}

/// <summary>
/// The rules for the notices on screen. Lists are newest first and are never changed in place; notices are matched by
/// reference when dismissed.
/// </summary>
public static class NoticeQueue
{
    /// <summary>At most this many toasts on screen.</summary>
    public const int MaxShown = 3;

    /// <summary>
    /// Shows <paramref name="notice"/> on top. A shown notice with the same non-null topic (or, without a topic, the same
    /// content) is replaced. Over <see cref="MaxShown"/>, the oldest self-dismissing notice goes first, then the oldest;
    /// the new one always stays.
    /// </summary>
    public static IReadOnlyList<Notice> Add(IReadOnlyList<Notice> shown, Notice notice)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(notice);
        var list = new List<Notice>(shown.Count + 1) { notice };
        foreach (var old in shown)
        {
            bool replaced = notice.Topic is { } topic ? old.Topic == topic : notice.SameContent(old);
            if (!replaced)
                list.Add(old);
        }
        while (list.Count > MaxShown)
        {
            int drop = list.FindLastIndex(n => n.AutoDismiss && !ReferenceEquals(n, notice));
            list.RemoveAt(drop > 0 ? drop : list.Count - 1);
        }
        return list;
    }

    /// <summary>Removes this notice (by reference), for example when it is closed or timed out.</summary>
    public static IReadOnlyList<Notice> Dismiss(IReadOnlyList<Notice> shown, Notice notice)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return shown.Where(n => !ReferenceEquals(n, notice)).ToList();
    }

    /// <summary>
    /// The notices in <paramref name="before"/> that are gone from <paramref name="after"/> (by reference): replaced,
    /// evicted over the cap, or dismissed. Whoever waits on a notice's answer learns it will not come.
    /// </summary>
    public static IReadOnlyList<Notice> Removed(IReadOnlyList<Notice> before, IReadOnlyList<Notice> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return before.Where(n => !after.Contains(n, ReferenceEqualityComparer.Instance)).ToList();
    }

    /// <summary>Removes the notices about <paramref name="topic"/> (the problem was fixed without a new notice).</summary>
    public static IReadOnlyList<Notice> DismissTopic(IReadOnlyList<Notice> shown, string topic)
    {
        ArgumentNullException.ThrowIfNull(shown);
        return shown.Where(n => n.Topic != topic).ToList();
    }
}

/// <summary>What changed about the picture source's window, worth a notice.</summary>
public enum SourceChange { None, Minimized, Lost, Found }

/// <summary>
/// Turns the pipeline's found/minimized status into "minimized", "lost" (after it was seen) and "found again" notices,
/// each said once. Not finding the window before it was ever seen is not news (the Home page covers it).
/// </summary>
public sealed class SourceWatch
{
    private bool _seen;
    private SourceChange _problem = SourceChange.None;

    public SourceChange Update(bool found, bool minimized)
    {
        if (found)
        {
            _seen = true;
            var now = minimized ? SourceChange.Minimized : SourceChange.None;
            if (now == _problem)
                return SourceChange.None;
            _problem = now;
            return minimized ? SourceChange.Minimized : SourceChange.Found;
        }
        if (!_seen || _problem == SourceChange.Lost)
            return SourceChange.None;
        _problem = SourceChange.Lost;
        return SourceChange.Lost;
    }

    /// <summary>The source changed: forget the window and any problem.</summary>
    public void Reset()
    {
        _seen = false;
        _problem = SourceChange.None;
    }
}
