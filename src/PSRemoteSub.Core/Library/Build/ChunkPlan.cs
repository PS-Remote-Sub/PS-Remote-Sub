using System.Globalization;

namespace PSRemoteSub.Core.Library.Build;

/// <summary>A unit of translation work: one agent translates one chunk.</summary>
public sealed record Chunk(int Number, IReadOnlyList<Segment> Lines)
{
    public string Name => Number.ToString("D4", CultureInfo.InvariantCulture);
}

public static class ChunkPlan
{
    /// <summary>
    /// Whole scenes packed in file order; a scene over <paramref name="max"/> is split into equal parts and gets its own chunks; scenes under <paramref name="small"/>
    /// share a chunk with the small scenes next to them in the same part, up to <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<Chunk> Plan(IReadOnlyList<Segment> segs, int max = 200, int small = 200)
    {
        ArgumentNullException.ThrowIfNull(segs);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var chunks = new List<List<Segment>>();
        List<Segment>? smallBin = null;
        string? smallPart = null;

        foreach (var scene in Scenes(segs))
        {
            var part = scene[0].Part;
            if (scene.Count < small && scene.Count <= max)
            {
                if (smallBin is null || smallPart != part || smallBin.Count + scene.Count > max)
                {
                    smallBin = [];
                    smallPart = part;
                    chunks.Add(smallBin);
                }
                smallBin.AddRange(scene);
                continue;
            }
            smallBin = null;
            var parts = (scene.Count + max - 1) / max;
            for (int p = 0, start = 0; p < parts; p++)
            {
                var size = (scene.Count - start) / (parts - p);
                chunks.Add(scene.GetRange(start, size));
                start += size;
            }
        }
        return chunks.Select((c, i) => new Chunk(i + 1, c)).ToList();
    }

    private static IEnumerable<List<Segment>> Scenes(IReadOnlyList<Segment> segs)
    {
        List<Segment>? cur = null;
        foreach (var s in segs)
        {
            if (cur is not null && (cur[0].Scene != s.Scene || cur[0].Part != s.Part))
            {
                yield return cur;
                cur = null;
            }
            (cur ??= []).Add(s);
        }
        if (cur is not null) yield return cur;
    }
}
