namespace PSRemoteSub.Core.Library.Build;

public static partial class BuildWork
{
    /// <summary>
    /// Rewrites <paramref name="libDir"/>/subtitles.jsonl from every chunk whose out file passes, in id order, then manifest.json.
    /// Returns the number of lines written.
    /// </summary>
    public static int Assemble(string workDir, string libDir, LibraryVersion version)
    {
        var lines = new List<(Segment Seg, string Th)>();
        foreach (var n in CheckAll(workDir).Passed)
        {
            var chunk = ReadChunk(workDir, n);
            var th = CheckOut(chunk, OutPath(workDir, n)).Rows.ToDictionary(r => r.Id, r => r.Th, StringComparer.Ordinal);
            lines.AddRange(chunk.Lines.Select(l => (l, th[l.Id])));
        }
        lines.Sort((a, b) => string.CompareOrdinal(a.Seg.Id, b.Seg.Id));

        Directory.CreateDirectory(libDir);
        var path = Path.Combine(libDir, LibraryFiles.Subtitles);
        var tmp = path + ".tmp";
        WriteJsonl(tmp, lines, (w, x) =>
        {
            w.WriteString("en", x.Seg.En);
            w.WriteString("th", x.Th);
            w.WriteString("source", "haiku");
            w.WriteString("scene", x.Seg.Scene);
            if (x.Seg.Speaker is not null) w.WriteString("speaker", x.Seg.Speaker);
        });
        File.Move(tmp, path, overwrite: true);
        LibraryPack.WriteVersion(libDir, version);
        return lines.Count;
    }
}
