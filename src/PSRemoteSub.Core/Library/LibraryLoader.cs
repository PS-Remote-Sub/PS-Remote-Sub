using System.Text.Json;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Library;

/// <summary>Reads a library folder (files in <see cref="LibraryFiles"/>). Never throws on bad data; logs counts only.</summary>
public static class LibraryLoader
{
    public static LibraryData? Load(string dir, string game)
    {
        try
        {
            var linesPath = Path.Combine(dir, LibraryFiles.Subtitles);
            var version = ReadVersion(dir, out var sha);
            if (version is null || !File.Exists(linesPath)) return null;

            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var lines = ReadLines(linesPath, "library", hash).ToList();
            var linesSha = Convert.ToHexStringLower(hash.GetHashAndReset());
            var glossary = ReadGlossary(Path.Combine(dir, LibraryFiles.Terms), game);
            var info = ReadGame(Path.Combine(dir, LibraryFiles.World), game);
            var style = ReadText(Path.Combine(dir, LibraryFiles.Guide));
            return new LibraryData(game, version.Value, sha, lines, glossary, info, style, linesSha);
        }
        catch (Exception ex)
        {
            Log.Warn($"Library {game}: load failed", ex);
            return null;
        }
    }

    /// <summary>The translation guide files alone (terms, world, guide), for building a library that has no lines yet.</summary>
    public static (IReadOnlyList<GlossaryTerm> Glossary, GameInfo Info, string Style) LoadGuide(string dir, string game) =>
        (ReadGlossary(Path.Combine(dir, LibraryFiles.Terms), game), ReadGame(Path.Combine(dir, LibraryFiles.World), game),
            ReadText(Path.Combine(dir, LibraryFiles.Guide)));

    public static LibraryVersion? ReadVersion(string dir) => ReadVersion(dir, out _);

    private static LibraryVersion? ReadVersion(string dir, out string? sha)
    {
        sha = null;
        try
        {
            var path = Path.Combine(dir, LibraryFiles.Manifest);
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            sha = Str(doc.RootElement, "subtitlesSha256");
            return LibraryVersion.TryParse(Str(doc.RootElement, "version"), out var v) ? v : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IEnumerable<LibraryLine> ReadLines(string path, string defaultSource) => ReadLines(path, defaultSource, null);

    /// <summary>Reads the lines; <paramref name="hash"/>, when given, receives every byte of the file.</summary>
    private static IEnumerable<LibraryLine> ReadLines(string path, string defaultSource, System.Security.Cryptography.IncrementalHash? hash)
    {
        if (!File.Exists(path)) yield break;
        var order = 0;
        var bad = 0;
        using var file = File.OpenRead(path);
        using var reader = new StreamReader(hash is null ? file : new HashingStream(file, hash));
        for (var raw = reader.ReadLine(); raw is not null; raw = reader.ReadLine())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            LibraryLine? line = null;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;
                var en = Str(r, "en");
                var th = Str(r, "th");
                if (en is not null && th is not null)
                    line = new LibraryLine(en, th, Str(r, "source") ?? defaultSource,
                        Str(r, "scene"), Str(r, "video"), Str(r, "speaker"), order);
            }
            catch (JsonException) { }

            if (line is null) { bad++; continue; }
            order++;
            yield return line;
        }
        if (bad > 0) Log.Warn($"Library: skipped {bad} bad lines in {Path.GetFileName(path)}");
    }

    private static List<GlossaryTerm> ReadGlossary(string path, string game)
    {
        var list = new List<GlossaryTerm>();
        if (!File.Exists(path)) return list;
        var bad = 0;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var en = Str(e, "en");
                var th = Str(e, "th");
                if (en is null || th is null) { bad++; continue; }
                list.Add(new GlossaryTerm(en, th, Str(e, "notes") ?? ""));
            }
        }
        catch (JsonException) { Log.Warn($"Library {game}: terms.json unreadable"); }
        if (bad > 0) Log.Warn($"Library {game}: skipped {bad} bad entries in terms.json");
        return list;
    }

    private static GameInfo ReadGame(string path, string game)
    {
        if (!File.Exists(path)) return GameInfo.Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return GameInfo.Empty;

            var chars = new List<GameCharacter>();
            var places = new List<GamePlace>();
            var bad = 0;
            if (r.TryGetProperty("characters", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    var en = Str(c, "en");
                    if (en is null) { bad++; continue; }
                    var aliases = new List<string>();
                    if (c.TryGetProperty("alsoCalled", out var al) && al.ValueKind == JsonValueKind.Array)
                        foreach (var a in al.EnumerateArray())
                            if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s) aliases.Add(s);
                    chars.Add(new GameCharacter(en, aliases, Str(c, "gender"), Str(c, "th"), Str(c, "thShort"),
                        Str(c, "speaksAs"), Str(c, "role"), Str(c, "notes")));
                }
            if (r.TryGetProperty("places", out var ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (var p in ps.EnumerateArray())
                {
                    var en = Str(p, "en");
                    if (en is null) { bad++; continue; }
                    places.Add(new GamePlace(en, Str(p, "th"), Str(p, "notes")));
                }
            if (bad > 0) Log.Warn($"Library {game}: skipped {bad} bad entries in world.json");
            return new GameInfo(Str(r, "title"), Str(r, "setting"), chars, places);
        }
        catch (JsonException)
        {
            Log.Warn($"Library {game}: world.json unreadable");
            return GameInfo.Empty;
        }
    }

    private static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}

/// <summary>A read-only pass-through stream that feeds every byte read into a hash.</summary>
internal sealed class HashingStream(Stream inner, System.Security.Cryptography.IncrementalHash hash) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        hash.AppendData(buffer[..n]);
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
