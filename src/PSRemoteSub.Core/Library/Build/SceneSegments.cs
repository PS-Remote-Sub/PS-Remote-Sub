namespace PSRemoteSub.Core.Library.Build;

/// <summary>One on-screen subtitle piece to translate, deduplicated by normalized text.</summary>
public sealed record Segment(string Id, string En, string Scene, string Part, string? Speaker);

public static class SceneSegments
{
    /// <summary>Main story first, then open world, then minigames, file order within a part; the first piece with a given normalized text owns it.</summary>
    public static IReadOnlyList<Segment> Build(IEnumerable<SceneRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var ordered = rows.Select((r, i) => (r, i)).OrderBy(x => PartRank(x.r.Part)).ThenBy(x => x.i).Select(x => x.r);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<Segment>();
        foreach (var row in ordered)
        {
            var speaker = string.IsNullOrWhiteSpace(row.Speaker) || row.Speaker.StartsWith("Unknown", StringComparison.Ordinal)
                ? null : row.Speaker;
            foreach (var piece in SubtitleMarkup.Pieces(row.Text))
            {
                var key = LibraryText.Normalize(piece);
                if (key.Length == 0 || !seen.Add(key)) continue;
                var id = "s" + (result.Count + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
                result.Add(new Segment(id, piece, row.SceneId, row.Part, speaker));
            }
        }
        return result;
    }

    internal static int PartRank(string part) =>
        part.StartsWith("Main story", StringComparison.Ordinal) ? 0
        : part.StartsWith("Open world", StringComparison.Ordinal) ? 1
        : part.StartsWith("Minigame", StringComparison.Ordinal) ? 2
        : 3;
}
