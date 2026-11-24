using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Languages;

namespace PSRemoteSub.Core.Text;

/// <summary>
/// Splits one read of a chat-style box (phone messages) into bubbles. A vertical gap bigger than
/// <see cref="ParagraphGap"/> times the median line height starts a new bubble; inside a bubble, a short capitalised
/// first line without punctuation is the sender's name.
/// </summary>
public static class BubbleSplitter
{
    /// <summary>A gap between two lines, in median line heights, above which a new bubble starts.</summary>
    public const double ParagraphGap = 1.5;

    private const string NameStoppers = ".,!?…:;";

    public static IReadOnlyList<SpeakerRead> Split(IReadOnlyList<OcrLine> lines, SourceLanguage lang)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(lang);
        var real = lines
            .Where(l => l.Bounds.Width > 0 && l.Bounds.Height > 0 && TextNormalizer.Normalize(l.Text).Length > 0)
            .OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X)
            .ToList();
        if (real.Count == 0)
            return [];

        var heights = real.Select(l => l.Bounds.Height).Order().ToList();
        double median = heights.Count % 2 == 1
            ? heights[heights.Count / 2]
            : (heights[heights.Count / 2 - 1] + heights[heights.Count / 2]) / 2.0;

        var paragraphs = new List<List<OcrLine>> { new() { real[0] } };
        for (int i = 1; i < real.Count; i++)
        {
            if (real[i].Bounds.Y - real[i - 1].Bounds.Bottom > ParagraphGap * median)
                paragraphs.Add([]);
            paragraphs[^1].Add(real[i]);
        }

        var reads = new List<SpeakerRead>();
        foreach (var paragraph in paragraphs)
        {
            var parts = SpeakerSplitter.Split(paragraph, lang);
            if (parts.Count == 1 && parts[0].Name is null && TrySender(paragraph, lang, out var name, out var text))
                reads.Add(new SpeakerRead(name, text, null, Cover(paragraph)));
            else
                reads.AddRange(parts);
        }
        return reads;
    }

    private static bool TrySender(List<OcrLine> paragraph, SourceLanguage lang, out string name, out string text)
    {
        name = text = "";
        if (paragraph.Count < 2)
            return false;
        var candidate = TextNormalizer.Normalize(paragraph[0].Text).Trim();
        int words = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words is < 1 or > 2 || !char.IsUpper(candidate[0]) || candidate.IndexOfAny(NameStoppers.ToCharArray()) >= 0)
            return false;
        int widest = paragraph.Skip(1).Max(l => l.Bounds.Width);
        if (paragraph[0].Bounds.Width * 2 > widest)
            return false;
        var rest = TextNormalizer.Normalize(
            LineJoiner.JoinParts(paragraph.Skip(1).Select(l => l.Text.Trim()), lang).Replace('\n', ' '));
        if (rest.Length == 0)
            return false;
        name = candidate;
        text = rest;
        return true;
    }

    private static PixelRect Cover(List<OcrLine> lines)
    {
        int x0 = lines.Min(l => l.Bounds.X), y0 = lines.Min(l => l.Bounds.Y);
        return new PixelRect(x0, y0, lines.Max(l => l.Bounds.Right) - x0, lines.Max(l => l.Bounds.Bottom) - y0);
    }
}
