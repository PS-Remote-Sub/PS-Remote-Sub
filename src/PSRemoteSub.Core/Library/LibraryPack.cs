using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PSRemoteSub.Core.Library;

/// <summary>Maintainer-side library tooling: append to subtitles.jsonl, pack a release zip.</summary>
public static class LibraryPack
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Appends the lines of <paramref name="pendingFile"/> to subtitles.jsonl (no scene), keeping each line's source when
    /// the maintainer set one, else "ai". Returns how many were added.
    /// </summary>
    public static int Append(string libraryDir, string pendingFile)
    {
        var seen = KnownKeys(libraryDir);
        var rows = new List<string>();
        foreach (var raw in File.ReadLines(pendingFile))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TryRead(raw, out var en, out var th, out var speaker, out _, out var source)) continue;
            if (th.Trim().Length == 0) continue;
            var key = LibraryText.Normalize(en);
            if (key.Length == 0 || !seen.Add(key)) continue;
            rows.Add(Json(w =>
            {
                w.WriteString("en", en);
                w.WriteString("th", th);
                w.WriteString("source", string.IsNullOrWhiteSpace(source) ? "ai" : source);
                if (!string.IsNullOrEmpty(speaker)) w.WriteString("speaker", speaker);
            }));
        }
        if (rows.Count == 0) return 0;

        var linesPath = Path.Combine(libraryDir, LibraryFiles.Subtitles);
        var sb = new StringBuilder();
        if (File.Exists(linesPath))
        {
            using var fs = File.OpenRead(linesPath);
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n') sb.Append('\n');
            }
        }
        foreach (var r in rows) sb.Append(r).Append('\n');
        File.AppendAllText(linesPath, sb.ToString(), Utf8);
        return rows.Count;
    }

    /// <summary>Rewrites manifest.json (version, line count, sha256 of subtitles.jsonl) and zips the library files flat. Returns the zip path.</summary>
    public static string Pack(string libraryDir, string game, LibraryVersion version, string outDir)
    {
        WriteVersion(libraryDir, version);

        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, $"library-{game}-{version}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var name in LibraryFiles.All)
        {
            var path = Path.Combine(libraryDir, name);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
        }
        return zipPath;
    }

    /// <summary>Rewrites manifest.json: the version, the line count and the sha256 of subtitles.jsonl.</summary>
    public static void WriteVersion(string libraryDir, LibraryVersion version)
    {
        var linesPath = Path.Combine(libraryDir, LibraryFiles.Subtitles);
        var linesBytes = File.ReadAllBytes(linesPath);
        var count = File.ReadLines(linesPath).Count(l => !string.IsNullOrWhiteSpace(l));
        var sha = Convert.ToHexString(SHA256.HashData(linesBytes)).ToLowerInvariant();

        var versionJson = Json(w =>
        {
            w.WriteString("version", version.ToString());
            w.WriteNumber("subtitleCount", count);
            w.WriteString("subtitlesSha256", sha);
        }, indented: true);
        File.WriteAllText(Path.Combine(libraryDir, LibraryFiles.Manifest), versionJson + "\n", Utf8);
    }

    private static HashSet<string> KnownKeys(string libraryDir)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in LibraryLoader.ReadLines(Path.Combine(libraryDir, LibraryFiles.Subtitles), "library"))
            seen.Add(LibraryText.Normalize(l.En));
        return seen;
    }

    private static bool TryRead(string raw, out string en, out string th, out string? speaker, out string? model,
        out string? source)
    {
        en = th = "";
        speaker = model = source = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (Str(r, "en") is not { } e || Str(r, "th") is not { } t) return false;
            en = e;
            th = t;
            speaker = Str(r, "speaker");
            model = Str(r, "model");
            source = Str(r, "source");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Json(Action<Utf8JsonWriter> body, bool indented = false)
    {
        using var ms = new MemoryStream();
        var options = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = indented, NewLine = "\n" };
        using (var w = new Utf8JsonWriter(ms, options))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Utf8.GetString(ms.ToArray());
    }
}
