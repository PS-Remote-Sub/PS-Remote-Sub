using System.Text;

namespace PSRemoteSub.Core.Library;

/// <summary>
/// The game notes sent to the AI with a line. <paramref name="Static"/> (setting and style) is the same for every line
/// of a game, so a provider can cache it; <paramref name="PerLine"/> is the glossary terms, characters and places that
/// this line touches.
/// </summary>
public sealed record GameBrief(string Static, string PerLine);

public static class GameBriefBuilder
{
    public const int CloudBudget = 6000;
    public const int OllamaBudget = 2000;
    public const int NoteMax = 400;

    /// <summary>
    /// <paramref name="budget"/> is the total characters of <c>Static + PerLine</c>. Static is sized from the budget alone (two thirds: the head of the style, cut at a line break, then the setting if it fits), so it is identical for every line and stays cacheable.
    /// PerLine then gets the rest, in the order glossary terms, speakers, characters mentioned, places; each item whole or not at all,
    /// stopping at the first that does not fit.
    /// </summary>
    public static GameBrief Build(LibraryData lib, CharacterIndex chars, string text, IReadOnlyList<string?> speakerLabels, int budget)
    {
        ArgumentNullException.ThrowIfNull(lib);
        ArgumentNullException.ThrowIfNull(chars);
        text ??= "";

        var items = new List<string>();

        var padded = " " + LibraryText.Normalize(text) + " ";
        foreach (var t in lib.Glossary)
        {
            var en = LibraryText.Normalize(t.En);
            if (en.Length == 0 || !padded.Contains(" " + en + " ", StringComparison.Ordinal)) continue;
            items.Add(string.IsNullOrWhiteSpace(t.Note) ? $"{t.En} → {t.Th}" : $"{t.En} → {t.Th} ({t.Note})");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in speakerLabels)
            if (chars.Resolve(label) is { } c && seen.Add(c.En)) items.Add(FormatCharacter(c));
        foreach (var c in chars.MentionedIn(text))
            if (seen.Add(c.En)) items.Add(FormatCharacter(c));

        foreach (var p in chars.PlacesIn(text))
        {
            var sb = new StringBuilder(p.En).Append(':');
            if (!string.IsNullOrWhiteSpace(p.Th)) sb.Append(' ').Append(p.Th);
            if (!string.IsNullOrWhiteSpace(p.Note)) sb.Append(" — ").Append(p.Note);
            items.Add(sb.ToString());
        }

        // Static depends only on the library and the budget, never on the line: it is the cached prompt prefix.
        var staticBudget = budget - budget / 3;
        var style = StyleHead(lib.Style ?? "", staticBudget);
        var stat = style;
        var setting = lib.Info.Setting?.Trim();
        if (!string.IsNullOrEmpty(setting))
        {
            var line = "Setting: " + setting;
            if (style.Length + line.Length + (style.Length > 0 ? 1 : 0) <= staticBudget)
                stat = style.Length > 0 ? line + "\n" + style : line;
        }

        var perLine = new StringBuilder();
        var room = budget - stat.Length;
        foreach (var item in items)
        {
            var add = item.Length + (perLine.Length > 0 ? 1 : 0);
            if (perLine.Length + add > room) break;
            if (perLine.Length > 0) perLine.Append('\n');
            perLine.Append(item);
        }
        return new GameBrief(stat, perLine.ToString());
    }

    private static string FormatCharacter(GameCharacter c)
    {
        var names = string.Join(" / ", new[] { c.ThName, c.ThCall }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var parts = new[] { names, c.Gender, c.Self }.Where(s => !string.IsNullOrWhiteSpace(s));
        var sb = new StringBuilder(c.En).Append(": ").Append(string.Join(", ", parts));
        if (!string.IsNullOrWhiteSpace(c.Note))
        {
            var note = c.Note.Length > NoteMax ? c.Note[..NoteMax] : c.Note;
            sb.Append(" — ").Append(note);
        }
        return sb.ToString().TrimEnd(' ', ':');
    }

    /// <summary>Whole lines from the top of the style that fit in <paramref name="max"/> characters.</summary>
    private static string StyleHead(string style, int max)
    {
        if (max <= 0) return "";
        style = style.Replace("\r\n", "\n").Trim();
        if (style.Length <= max) return style;
        var cut = style.LastIndexOf('\n', max);   // a line break at index <= max keeps the text before it within max
        return cut <= 0 ? "" : style[..cut].TrimEnd();
    }
}
