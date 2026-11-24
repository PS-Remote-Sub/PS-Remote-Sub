using PSRemoteSub.Core.Hub;
using PSRemoteSub.Core.Imaging;

namespace PSRemoteSub.Core.Layout;

/// <summary>One line of the caption, drawn in its own box: who says it (if known) and the text.</summary>
/// <param name="Cover">Where the game drew this line's source text (fraction of the client area), for the box to hide; null when unknown.</param>
public sealed record CaptionRow(SpeakerTag? Speaker, string Text, RegionFraction? Cover = null);

/// <summary>
/// Splits a caption into the rows the overlay draws as separate boxes, like the game draws each speaker's line on its
/// own background with a gap between them.
/// </summary>
public static class CaptionRows
{
    /// <summary>
    /// One row per text line (<c>\n</c>), paired with <paramref name="speakers"/> by index, and with
    /// <paramref name="rowBounds"/> by index when it has one entry per line (else no row gets a cover). Blank lines are
    /// dropped without shifting either; an empty caption is one empty row.
    /// </summary>
    public static IReadOnlyList<CaptionRow> Split(string text, IReadOnlyList<SpeakerTag?>? speakers,
        IReadOnlyList<RegionFraction>? rowBounds = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.Split('\n');
        bool covered = rowBounds is not null && rowBounds.Count == lines.Length;
        var rows = new List<CaptionRow>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
                continue;
            rows.Add(new CaptionRow(speakers is not null && i < speakers.Count ? speakers[i] : null, line,
                covered ? rowBounds![i] : null));
        }
        if (rows.Count == 0)
            rows.Add(new CaptionRow(null, text.Trim()));
        return rows;
    }
}
