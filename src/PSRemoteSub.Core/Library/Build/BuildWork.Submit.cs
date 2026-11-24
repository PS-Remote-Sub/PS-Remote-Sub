using System.Text;
using System.Text.Json;

namespace PSRemoteSub.Core.Library.Build;

public sealed record CheckSummary(
    int Total, IReadOnlyList<int> Passed, IReadOnlyDictionary<int, IReadOnlyList<string>> Failed, IReadOnlyList<int> Missing);

public static partial class BuildWork
{
    /// <summary>What an agent reads: the chunk's guide, a <c>---</c> line, then one <c>id TAB english</c> line per piece.</summary>
    public static string Show(string workDir, string libDir, int number)
    {
        var chunk = ReadChunk(workDir, number);
        var (glossary, info, style) = LibraryLoader.LoadGuide(libDir, Path.GetFileName(libDir));
        var sb = new StringBuilder(ChunkGuide.Render(chunk, glossary, info, style));
        sb.Append("\n---\n");
        foreach (var l in chunk.Lines) sb.Append(l.Id).Append('\t').Append(l.En).Append('\n');
        return sb.ToString();
    }

    /// <summary>Checks draft/NNNN.txt; only a passing draft becomes out/NNNN.jsonl (written to a temp file, then moved).</summary>
    public static DraftResult Submit(string workDir, int number)
    {
        var chunk = ReadChunk(workDir, number);
        var draft = DraftPath(workDir, number);
        if (!File.Exists(draft)) return new DraftResult(false, [$"no draft file {draft}"], []);
        var result = DraftCheck.Check(chunk, File.ReadAllText(draft, Encoding.UTF8));
        if (!result.Ok) return result;

        Directory.CreateDirectory(OutDir(workDir));
        var path = OutPath(workDir, number);
        var tmp = path + ".tmp";
        WriteJsonl(tmp, result.Rows, (w, r) =>
        {
            w.WriteString("id", r.Id);
            w.WriteString("th", r.Th);
        });
        File.Move(tmp, path, overwrite: true);
        return result;
    }

    /// <summary>Re-checks every out file against its chunk and writes status.json.</summary>
    public static CheckSummary CheckAll(string workDir)
    {
        var total = ChunkCount(workDir);
        var passed = new List<int>();
        var missing = new List<int>();
        var failed = new SortedDictionary<int, IReadOnlyList<string>>();
        for (var n = 1; n <= total; n++)
        {
            var path = OutPath(workDir, n);
            if (!File.Exists(path)) { missing.Add(n); continue; }
            var r = CheckOut(ReadChunk(workDir, n), path);
            if (r.Ok) passed.Add(n);
            else failed[n] = r.Problems;
        }

        File.WriteAllText(Path.Combine(workDir, "status.json"), Json(w =>
        {
            w.WriteNumber("total", total);
            w.WriteNumber("passed", passed.Count);
            w.WriteStartArray("missing");
            foreach (var n in missing) w.WriteNumberValue(n);
            w.WriteEndArray();
            w.WriteStartObject("failed");
            foreach (var (n, problems) in failed)
            {
                w.WriteStartArray(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var p in problems) w.WriteStringValue(p);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }) + "\n", Utf8);
        return new CheckSummary(total, passed, failed, missing);
    }

    internal static DraftResult CheckOut(Chunk chunk, string outPath)
    {
        var draft = new StringBuilder();
        try
        {
            foreach (var raw in File.ReadLines(outPath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                using var doc = JsonDocument.Parse(raw);
                draft.Append(doc.RootElement.GetProperty("id").GetString()).Append('\t')
                    .Append(doc.RootElement.GetProperty("th").GetString()).Append('\n');
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new DraftResult(false, ["out file unreadable: " + ex.Message], []);
        }
        return DraftCheck.Check(chunk, draft.ToString());
    }
}
