using System.Text.RegularExpressions;
using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Text;

/// <summary>One speaker's part of a subtitle: the name from its label (if any) and what they say.</summary>
/// <param name="LabelBox">The name label's words (through the colon), in OCR-image pixels; null without word boxes.</param>
/// <param name="Box">All of this part's lines, in OCR-image pixels: where the game drew it. Null without line positions.</param>
public sealed record SpeakerRead(string? Name, string Text, PixelRect? LabelBox, PixelRect? Box = null);

/// <summary>
/// Splits OCR lines into speaker parts. A line that starts with a name label (<c>Lucia:</c>) or a dash starts a new
/// part; any other line is a wrapped continuation of the part above it.
/// </summary>
public static partial class SpeakerSplitter
{
    private const string WordTail = @"[\p{L}\p{N}'’.\-・]*";
    private const int MaxNameLength = 25;

    // One to three words, each capitalised ("Mattia", "Mr. Smith", "LUCIA"), so a sentence before a colon
    // ("Remember this:", "He said:") is not a name.
    [GeneratedRegex(@"^(?<name>\p{Lu}" + WordTail + @"(?: \p{Lu}" + WordTail + @"){0,2})\s*[:：]\s*(?<rest>\S.*)$")]
    private static partial Regex CasedLabel();

    [GeneratedRegex(@"^(?<name>\p{L}" + WordTail + @"(?: \p{L}" + WordTail + @"){0,2})\s*[:：]\s*(?<rest>\S.*)$")]
    private static partial Regex UncasedLabel();

    [GeneratedRegex(@"^[-–—]\s*\S")]
    private static partial Regex Dash();

    public static IReadOnlyList<SpeakerRead> Split(IReadOnlyList<OcrLine> lines, SourceLanguage lang)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(lang);
        var groups = new List<Group>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
        {
            var text = line.Text.Trim();
            if (TryLabel(text, lang, out var name, out var rest))
                groups.Add(new Group(name, [rest], LabelBox(line)));
            else if (Dash().IsMatch(text) || groups.Count == 0)
                groups.Add(new Group(null, [text], null));
            else
                groups[^1].Parts.Add(text);
            groups[^1].Cover(line);
        }

        var reads = new List<SpeakerRead>(groups.Count);
        foreach (var g in groups)
        {
            var joined = TextNormalizer.Normalize(LineJoiner.JoinParts(g.Parts, lang).Replace('\n', ' '));
            if (joined.Length > 0)
                reads.Add(new SpeakerRead(g.Name, joined, g.Label, g.Box));
        }
        return reads;
    }

    private sealed class Group(string? name, List<string> parts, PixelRect? label)
    {
        public string? Name { get; } = name;
        public List<string> Parts { get; } = parts;
        public PixelRect? Label { get; } = label;
        public PixelRect? Box { get; private set; }

        /// <summary>Adds a line to the box, if it has a position and real text (as <c>SubtitleBounds</c> counts lines).</summary>
        public void Cover(OcrLine line)
        {
            var b = line.Bounds;
            if (b.Width <= 0 || b.Height <= 0 || TextNormalizer.Normalize(line.Text).Length == 0)
                return;
            if (Box is not { } box)
            {
                Box = b;
                return;
            }
            int x0 = Math.Min(box.X, b.X), y0 = Math.Min(box.Y, b.Y);
            Box = new PixelRect(x0, y0, Math.Max(box.Right, b.Right) - x0, Math.Max(box.Bottom, b.Bottom) - y0);
        }
    }

    /// <summary>
    /// With <paramref name="enabled"/>: only the parts that have a speaker name. Text without a name in the subtitle box
    /// (signs, shop names, number plates) is dropped, and a read with no name at all becomes empty, so it is neither
    /// shown nor translated. Without it, <paramref name="reads"/> as they are.
    /// </summary>
    public static IReadOnlyList<SpeakerRead> SpeakerLinesOnly(IReadOnlyList<SpeakerRead> reads, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(reads);
        return enabled ? [.. reads.Where(r => r.Name is not null)] : reads;
    }

    /// <summary>The subtitle's source text: one line per speaker, <c>Name: text</c>, joined with <c>\n</c>.</summary>
    public static string Compose(IReadOnlyList<SpeakerRead> reads)
    {
        ArgumentNullException.ThrowIfNull(reads);
        return string.Join('\n', reads.Select(r => r.Name is null ? r.Text : $"{r.Name}: {r.Text}"));
    }

    /// <summary>
    /// A name label: a letter (upper case for scripts with case, except ja/zh/ko), up to 24 more name characters, a
    /// colon (<c>:</c> or <c>：</c>), then text. Times (<c>10:30</c>) and lower-case words (<c>note:</c>) are not labels.
    /// </summary>
    public static bool TryLabel(string line, SourceLanguage lang, out string name, out string rest)
    {
        var m = (lang.Code is "ja" or "zh" or "ko" ? UncasedLabel() : CasedLabel()).Match(line);
        name = m.Success ? m.Groups["name"].Value.Trim() : "";
        rest = m.Success ? m.Groups["rest"].Value.Trim() : "";
        return m.Success && name.Length is > 0 and <= MaxNameLength;
    }

    /// <summary>The words up to and including the first one with a colon.</summary>
    private static PixelRect? LabelBox(OcrLine line)
    {
        if (line.Words is not { Count: > 0 } words)
            return null;
        int last = -1;
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Text.Contains(':') || words[i].Text.Contains('：'))
            {
                last = i;
                break;
            }
        }
        if (last < 0)
            return null;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
        for (int i = 0; i <= last; i++)
        {
            var b = words[i].Bounds;
            x0 = Math.Min(x0, b.X);
            y0 = Math.Min(y0, b.Y);
            x1 = Math.Max(x1, b.Right);
            y1 = Math.Max(y1, b.Bottom);
        }
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
