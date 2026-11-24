using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PSRemoteSub.Core.Library.Build;

public readonly record struct ImportResult(int Rows, int Segments, int Chunks);

/// <summary>The on-disk work folder of a library build: segments, chunks, agent drafts, checked output.</summary>
public static partial class BuildWork
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    internal static string ChunksDir(string work) => Path.Combine(work, "chunks");
    internal static string OutDir(string work) => Path.Combine(work, "out");
    internal static string DraftDir(string work) => Path.Combine(work, "draft");
    internal static string ChunkPath(string work, int n) => Path.Combine(ChunksDir(work), Name(n) + ".jsonl");
    internal static string OutPath(string work, int n) => Path.Combine(OutDir(work), Name(n) + ".jsonl");
    internal static string DraftPath(string work, int n) => Path.Combine(DraftDir(work), Name(n) + ".txt");
    private static string Name(int n) => n.ToString("D4", CultureInfo.InvariantCulture);

    public static ImportResult Import(string csvPath, string workDir, int maxChunk = 200)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(csvPath)));
        var manifest = Path.Combine(workDir, "manifest.json");

        List<SceneRow> rows;
        using (var reader = new StreamReader(csvPath, Encoding.UTF8))
            rows = SceneCsv.Read(reader).ToList();
        var segments = SceneSegments.Build(rows);
        var chunks = ChunkPlan.Plan(segments, maxChunk, maxChunk);

        // out/ rows bind Thai to ids only: the English behind every id must never change under existing results.
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(workDir, "segments.jsonl")] = Jsonl(segments, WriteSegment),
        };
        foreach (var c in chunks) files[ChunkPath(workDir, c.Number)] = Jsonl(c.Lines, WriteSegment);
        var oldChunks = Directory.Exists(ChunksDir(workDir)) ? Directory.GetFiles(ChunksDir(workDir), "*.jsonl") : [];
        var same = oldChunks.Length == chunks.Count
            && files.All(f => File.Exists(f.Key) && File.ReadAllBytes(f.Key).AsSpan().SequenceEqual(f.Value));
        if (same) return new ImportResult(rows.Count, segments.Count, chunks.Count);
        var hasOut = Directory.Exists(OutDir(workDir)) && Directory.EnumerateFiles(OutDir(workDir)).Any();
        if (hasOut) throw new InvalidOperationException("segments or chunks would change under existing results in out/");

        Directory.CreateDirectory(workDir);
        if (Directory.Exists(ChunksDir(workDir))) Directory.Delete(ChunksDir(workDir), true);
        Directory.CreateDirectory(ChunksDir(workDir));
        foreach (var (path, bytes) in files) File.WriteAllBytes(path, bytes);
        File.WriteAllText(manifest, Json(w =>
        {
            w.WriteString("csv_sha256", sha);
            w.WriteNumber("segments", segments.Count);
            w.WriteNumber("chunks", chunks.Count);
        }) + "\n", Utf8);
        return new ImportResult(rows.Count, segments.Count, chunks.Count);
    }

    public static int ChunkCount(string workDir) =>
        Directory.Exists(ChunksDir(workDir)) ? Directory.GetFiles(ChunksDir(workDir), "*.jsonl").Length : 0;

    public static Chunk ReadChunk(string workDir, int number)
    {
        var lines = new List<Segment>();
        foreach (var raw in File.ReadLines(ChunkPath(workDir, number), Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = JsonDocument.Parse(raw);
            var e = doc.RootElement;
            lines.Add(new Segment(e.GetProperty("id").GetString()!, e.GetProperty("en").GetString()!,
                e.GetProperty("scene").GetString()!, e.GetProperty("part").GetString()!,
                e.TryGetProperty("speaker", out var sp) ? sp.GetString() : null));
        }
        return new Chunk(number, lines);
    }

    private static void WriteSegment(Utf8JsonWriter w, Segment s)
    {
        w.WriteString("id", s.Id);
        w.WriteString("en", s.En);
        w.WriteString("scene", s.Scene);
        w.WriteString("part", s.Part);
        if (s.Speaker is not null) w.WriteString("speaker", s.Speaker);
    }

    private static byte[] Jsonl<T>(IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        using var ms = new MemoryStream();
        WriteJsonl(ms, items, write);
        return ms.ToArray();
    }

    internal static void WriteJsonl<T>(string path, IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        using var stream = File.Create(path);
        WriteJsonl(stream, items, write);
    }

    private static void WriteJsonl<T>(Stream stream, IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
    {
        foreach (var item in items)
        {
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Encoder }))
            {
                w.WriteStartObject();
                write(w, item);
                w.WriteEndObject();
            }
            stream.WriteByte((byte)'\n');
        }
    }

    internal static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = Encoder, Indented = true }))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
