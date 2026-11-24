using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Library;

/// <summary>Downloads newer library zips from GitHub Releases (tags <c>lib-YYYYMMDD.N</c>) into the user folder.</summary>
public sealed partial class LibraryUpdater(HttpClient http, LibraryStore store, string repositoryUrl)
{
    /// <summary>File names of the library format before <see cref="LibraryFiles"/>; removed once a new-format library is in.</summary>
    private static readonly string[] OldFormatFiles = ["version.json", "lines.jsonl", "glossary.json", "game.json", "style.md"];

    [GeneratedRegex(@"^lib-(\d{8}\.\d+)$")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"^library-([A-Za-z0-9_][A-Za-z0-9_.\-]*?)-(\d{8}\.\d+)\.zip$")]
    private static partial Regex AssetRx();

    [GeneratedRegex(@"^https://github\.com/([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+?)(?:\.git)?/?$")]
    private static partial Regex RepoRx();

    public async Task<IReadOnlyList<LibraryListing>> CheckAndInstallAsync(CancellationToken ct)
    {
        var installed = new List<LibraryListing>();
        try
        {
            var m = RepoRx().Match((repositoryUrl ?? "").Trim());
            if (!m.Success)
            {
                Log.Info("Library update: no repository configured");
                return installed;
            }

            var release = await FindReleaseAsync(m.Groups[1].Value, m.Groups[2].Value, ct);
            if (release is null) return installed;

            foreach (var (game, version, url) in release)
            {
                ct.ThrowIfCancellationRequested();
                var current = store.VersionOf(game);
                if (current is { } c && version <= c) continue;
                try
                {
                    var listing = await InstallAsync(game, version, url, ct);
                    if (listing is not null) installed.Add(listing);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn($"Library {game}: install failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            Log.Warn("Library update failed", ex);
        }
        return installed;
    }

    private async Task<List<(string Game, LibraryVersion Version, string Url)>?> FindReleaseAsync(
        string owner, string repo, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30");
        req.Headers.UserAgent.ParseAdd("PSRemoteSub");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"Library update: release list returned HTTP {(int)resp.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        JsonElement? best = null;
        LibraryVersion bestVersion = default;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.ValueKind != JsonValueKind.Object || Str(rel, "tag_name") is not { } tag) continue;
            var tm = TagRx().Match(tag);
            if (!tm.Success || !LibraryVersion.TryParse(tm.Groups[1].Value, out var v)) continue;
            if (best is null || v > bestVersion) { best = rel; bestVersion = v; }
        }
        if (best is not { } chosen) return null;

        var assets = new List<(string, LibraryVersion, string)>();
        if (chosen.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var name = Str(a, "name");
                var url = Str(a, "browser_download_url");
                if (name is null || url is null) continue;
                var am = AssetRx().Match(name);
                if (!am.Success || !LibraryVersion.TryParse(am.Groups[2].Value, out var av)) continue;
                assets.Add((am.Groups[1].Value, av, url));
            }
        return assets;
    }

    private async Task<LibraryListing?> InstallAsync(string game, LibraryVersion version, string url, CancellationToken ct)
    {
        var tmp = store.UserDir(game) + ".tmp";
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("PSRemoteSub");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Library {game}: download returned HTTP {(int)resp.StatusCode}");
                return null;
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            Extract(bytes, tmp);
            if (!Verify(game, version, tmp)) return null;

            var dest = store.UserDir(game);
            Directory.CreateDirectory(dest);
            if (!Swap(game, tmp, dest)) return null;

            var title = store.List().FirstOrDefault(l => l.Game == game)?.Title;
            Log.Info($"Library {game}: installed {version}");
            return new LibraryListing(game, title, version, dest);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception) { }
        }
    }

    /// <summary>Internal test seam: called with the file name just before each new file is moved in.</summary>
    internal Action<string>? BeforeMove { get; set; }

    /// <summary>Internal test seam: called with the file name just before a rollback restores it.</summary>
    internal Action<string>? BeforeRestore { get; set; }

    /// <summary>Replaces the 5 library files in dest with those in tmp; rolls back on any failure. Other files in dest are never touched.</summary>
    private bool Swap(string game, string tmp, string dest)
    {
        var bak = dest + ".bak";
        var placed = new List<string>();
        var backed = new List<string>();
        try
        {
            if (Directory.Exists(bak)) Directory.Delete(bak, true);
            Directory.CreateDirectory(bak);
            foreach (var f in LibraryFiles.All)
            {
                var old = Path.Combine(dest, f);
                if (!File.Exists(old)) continue;
                File.Move(old, Path.Combine(bak, f));
                backed.Add(f);
            }
            // subtitles.jsonl first, manifest.json last, so a half-finished copy never claims the new version.
            foreach (var f in LibraryFiles.All.OrderBy(f => f == LibraryFiles.Manifest))
            {
                var src = Path.Combine(tmp, f);
                if (!File.Exists(src)) continue;
                BeforeMove?.Invoke(f);
                File.Move(src, Path.Combine(dest, f));
                placed.Add(f);
            }
            try { Directory.Delete(bak, true); } catch (Exception) { }
            foreach (var f in OldFormatFiles)
                try { File.Delete(Path.Combine(dest, f)); } catch (Exception) { }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Library {game}: install failed, rolling back", ex);
            foreach (var f in placed)
                try { File.Delete(Path.Combine(dest, f)); } catch (Exception) { }
            var allRestored = true;
            foreach (var f in backed)
                try
                {
                    BeforeRestore?.Invoke(f);
                    File.Move(Path.Combine(bak, f), Path.Combine(dest, f), true);
                }
                catch (Exception) { allRestored = false; }
            if (allRestored)
                try { if (Directory.Exists(bak)) Directory.Delete(bak, true); } catch (Exception) { }
            else
                Log.Warn($"Library {game}: rollback could not restore every file; backup kept");
            return false;
        }
    }

    private static void Extract(byte[] zipBytes, string tmp)
    {
        var tmpFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0 || entry.FullName != entry.Name) continue; // ignore folders/subfolders
            if (!LibraryFiles.All.Contains(entry.Name, StringComparer.Ordinal)) continue;
            var target = Path.GetFullPath(Path.Combine(tmp, entry.Name));
            if (!target.StartsWith(tmpFull, StringComparison.OrdinalIgnoreCase)) continue;
            entry.ExtractToFile(target, true);
        }
    }

    private static bool Verify(string game, LibraryVersion expected, string tmp)
    {
        var linesPath = Path.Combine(tmp, LibraryFiles.Subtitles);
        var versionPath = Path.Combine(tmp, LibraryFiles.Manifest);
        if (!File.Exists(linesPath) || !File.Exists(versionPath))
        {
            Log.Warn($"Library {game}: zip is missing required files");
            return false;
        }
        if (LibraryLoader.ReadVersion(tmp) != expected)
        {
            Log.Warn($"Library {game}: manifest.json does not match the release");
            return false;
        }

        string? sha;
        using (var doc = JsonDocument.Parse(File.ReadAllText(versionPath)))
            sha = Str(doc.RootElement, "subtitlesSha256");
        string actual;
        using (var fs = File.OpenRead(linesPath))
            actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(sha?.Trim(), actual, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Library {game}: subtitles.jsonl checksum mismatch");
            return false;
        }
        return true;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
