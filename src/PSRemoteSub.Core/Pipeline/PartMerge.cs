using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Pipeline;

/// <summary>
/// A line of parts (bubbles) of which some are already translated: only the unknown parts go to the AI, one per line,
/// and its answer is merged back in order.
/// </summary>
public static class PartMerge
{
    /// <summary>The unknown parts' text (those whose <paramref name="known"/> is null), joined with <c>\n</c>.</summary>
    public static string Unknown(IReadOnlyList<string> parts, IReadOnlyList<string?> known)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(known);
        if (parts.Count != known.Count)
            throw new ArgumentException("One known entry per part.", nameof(known));
        return string.Join('\n', parts.Where((_, i) => known[i] is null));
    }

    /// <summary>
    /// <paramref name="known"/>[i] where set, else the answer's lines in order (trimmed, blank lines dropped), joined
    /// with <c>\n</c>. Null when the answer's line count differs from the number of unknown parts.
    /// </summary>
    public static string? Merge(IReadOnlyList<string?> known, string answer)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(answer);
        var lines = AnswerLines(answer);
        if (lines.Count != known.Count(k => k is null))
            return null;
        int next = 0;
        return string.Join('\n', known.Select(k => k ?? lines[next++]));
    }

    /// <summary>The answer's lines: trimmed, blank lines dropped.</summary>
    internal static IReadOnlyList<string> AnswerLines(string answer)
    {
        var clean = SpeakerText.Clean(answer);
        return clean.Length == 0 ? [] : clean.Split('\n');
    }
}
